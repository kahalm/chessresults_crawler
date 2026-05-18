using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChessResultsCrawler.Services;

public class CrawlerService
{
    private readonly HttpClient _httpClient;
    private readonly HtmlParserService _parser;
    private readonly AppDbContext _db;
    private readonly ILogger<CrawlerService> _logger;
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private const int DelayMs = 1500;

    public CrawlerService(HttpClient httpClient, HtmlParserService parser, AppDbContext db, ILogger<CrawlerService> logger)
    {
        _httpClient = httpClient;
        _parser = parser;
        _db = db;
        _logger = logger;
    }

    public async Task<CrawlJob> ExecuteCrawlAsync(CrawlJob job)
    {
        job.Status = CrawlJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        try
        {
            // Resolve tournament base URL and SNode
            var baseUrl = $"https://chess-results.com/tnr{job.ChessResultsId}.aspx?lan=0";
            var (resolvedUrl, html) = await FetchWithRedirectAsync(baseUrl);
            var sNode = HtmlParserService.ExtractSNode(resolvedUrl);

            // Find or create tournament
            var tournament = await _db.Tournaments
                .FirstOrDefaultAsync(t => t.ChessResultsId == job.ChessResultsId);

            if (tournament is null)
            {
                var name = await _parser.ParseTournamentNameAsync(html) ?? $"Tournament {job.ChessResultsId}";
                tournament = new Tournament
                {
                    ChessResultsId = job.ChessResultsId,
                    Name = name,
                    BaseUrl = resolvedUrl,
                    SNode = sNode
                };
                _db.Tournaments.Add(tournament);
                await _db.SaveChangesAsync();
            }
            else
            {
                tournament.BaseUrl = resolvedUrl;
                tournament.SNode = sNode;
                tournament.UpdatedAt = DateTime.UtcNow;
            }

            job.TournamentId = tournament.Id;

            // Get total rounds from art=0
            var art0Html = await FetchPageAsync(resolvedUrl, "art=0");
            var totalRounds = await _parser.ParseTotalRoundsAsync(art0Html);
            if (totalRounds.HasValue)
                tournament.TotalRounds = totalRounds.Value;

            switch (job.JobType)
            {
                case CrawlJobType.Full:
                    await CrawlPlayersAsync(tournament, resolvedUrl);
                    await CrawlAllPairingsAsync(tournament, resolvedUrl);
                    break;
                case CrawlJobType.PlayersOnly:
                    await CrawlPlayersAsync(tournament, resolvedUrl);
                    break;
                case CrawlJobType.PairingsOnly:
                    await CrawlAllPairingsAsync(tournament, resolvedUrl);
                    break;
                case CrawlJobType.CheckNewRounds:
                    // Handled by RoundDetectionService
                    break;
            }

            await _db.SaveChangesAsync();
            job.Status = CrawlJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Crawl failed for {ChessResultsId}", job.ChessResultsId);
            job.Status = CrawlJobStatus.Failed;
            job.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 2000)];
            job.CompletedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return job;
    }

    private async Task CrawlPlayersAsync(Tournament tournament, string baseUrl)
    {
        _logger.LogInformation("Crawling players for tournament {Id}", tournament.ChessResultsId);

        // Try art=16 (team tournaments full list), fall back to art=0 (individual tournaments)
        var html = await FetchPageAsync(baseUrl, "art=16&zeilen=99999");
        var parsedPlayers = await _parser.ParsePlayerListAsync(html);
        _logger.LogInformation("art=16: parsed {Count} players", parsedPlayers.Count);

        if (parsedPlayers.Count == 0)
        {
            html = await FetchPageAsync(baseUrl, "art=0&zeilen=99999");
            parsedPlayers = await _parser.ParsePlayerListAsync(html);
            _logger.LogInformation("art=0: parsed {Count} players", parsedPlayers.Count);
        }

        // Load existing teams for name-matching
        var existingTeams = await _db.Teams
            .Where(t => t.TournamentId == tournament.Id)
            .ToDictionaryAsync(t => t.Name);

        var existingPlayers = await _db.Players
            .Where(p => p.TournamentId == tournament.Id)
            .ToDictionaryAsync(p => p.Snr);

        int teamSnr = existingTeams.Values.Any()
            ? existingTeams.Values.Max(t => t.Snr) + 1
            : 1;

        foreach (var pp in parsedPlayers)
        {
            Team? team = null;
            if (!string.IsNullOrWhiteSpace(pp.TeamName))
            {
                if (!existingTeams.TryGetValue(pp.TeamName, out team))
                {
                    team = new Team
                    {
                        TournamentId = tournament.Id,
                        Snr = teamSnr++,
                        Name = pp.TeamName
                    };
                    _db.Teams.Add(team);
                    existingTeams[pp.TeamName] = team;
                }
            }

            if (existingPlayers.TryGetValue(pp.Snr, out var existingPlayer))
            {
                // Update
                existingPlayer.Name = pp.Name;
                existingPlayer.Title = pp.Title;
                existingPlayer.FideId = pp.FideId;
                existingPlayer.Elo = pp.Elo;
                existingPlayer.Country = pp.Country;
                existingPlayer.BoardNumber = pp.BoardNumber;
                existingPlayer.Team = team;
            }
            else
            {
                var player = new Player
                {
                    TournamentId = tournament.Id,
                    Snr = pp.Snr,
                    Name = pp.Name,
                    Title = pp.Title,
                    FideId = pp.FideId,
                    Elo = pp.Elo,
                    Country = pp.Country,
                    BoardNumber = pp.BoardNumber,
                    Team = team
                };
                _db.Players.Add(player);
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task CrawlAllPairingsAsync(Tournament tournament, string baseUrl)
    {
        _logger.LogInformation("Crawling pairings for tournament {Id}", tournament.ChessResultsId);

        // First discover available rounds from art=2
        var art2Html = await FetchPageAsync(baseUrl, "art=2");
        var availableRounds = await _parser.ParseAvailableRoundsAsync(art2Html);

        if (availableRounds.Count == 0 && tournament.TotalRounds > 0)
        {
            availableRounds = Enumerable.Range(1, tournament.TotalRounds).ToList();
        }

        // Detect tournament type from first round page
        var isTeam = await _parser.IsTeamPairingsPageAsync(art2Html);
        _logger.LogInformation("Tournament {Id} pairings type: {Type}", tournament.ChessResultsId, isTeam ? "Team" : "Individual");

        if (isTeam)
        {
            await CrawlTeamPairingsAsync(tournament, baseUrl, availableRounds);
        }
        else
        {
            await CrawlIndividualPairingsAsync(tournament, baseUrl, availableRounds);
        }
    }

    private async Task CrawlTeamPairingsAsync(Tournament tournament, string baseUrl, List<int> availableRounds)
    {
        var teams = await _db.Teams
            .Where(t => t.TournamentId == tournament.Id)
            .ToDictionaryAsync(t => t.Name);

        foreach (var roundNum in availableRounds)
        {
            var round = await GetOrCreateRoundAsync(tournament.Id, roundNum);

            var roundHtml = await FetchPageAsync(baseUrl, $"art=2&rd={roundNum}");
            var parsedPairings = await _parser.ParseTeamPairingsAsync(roundHtml);
            _logger.LogInformation("Round {Round}: parsed {Count} team pairings", roundNum, parsedPairings.Count);

            // Remove existing pairings for this round (re-crawl)
            var existingPairings = await _db.TeamPairings
                .Where(tp => tp.RoundId == round.Id)
                .ToListAsync();
            _db.TeamPairings.RemoveRange(existingPairings);

            foreach (var pp in parsedPairings)
            {
                var homeTeam = teams.GetValueOrDefault(pp.HomeTeamName);
                var awayTeam = teams.GetValueOrDefault(pp.AwayTeamName);

                if (homeTeam is null || awayTeam is null)
                {
                    _logger.LogWarning("Team not found: {Home} vs {Away}", pp.HomeTeamName, pp.AwayTeamName);
                    continue;
                }

                _db.TeamPairings.Add(new TeamPairing
                {
                    RoundId = round.Id,
                    MatchNumber = pp.MatchNumber,
                    HomeTeamId = homeTeam.Id,
                    AwayTeamId = awayTeam.Id,
                    HomeScore = pp.HomeScore,
                    AwayScore = pp.AwayScore
                });
            }

            round.ResultsPublished = parsedPairings.Any(p => p.HomeScore.HasValue);
            await _db.SaveChangesAsync();
        }
    }

    private async Task CrawlIndividualPairingsAsync(Tournament tournament, string baseUrl, List<int> availableRounds)
    {
        var playersBySnr = await _db.Players
            .Where(p => p.TournamentId == tournament.Id)
            .ToDictionaryAsync(p => p.Snr);

        foreach (var roundNum in availableRounds)
        {
            var round = await GetOrCreateRoundAsync(tournament.Id, roundNum);

            var roundHtml = await FetchPageAsync(baseUrl, $"art=2&rd={roundNum}");
            var parsedPairings = await _parser.ParseIndividualPairingsAsync(roundHtml);
            _logger.LogInformation("Round {Round}: parsed {Count} individual pairings", roundNum, parsedPairings.Count);

            // Remove existing pairings for this round (re-crawl)
            var existingPairings = await _db.Pairings
                .Where(p => p.RoundId == round.Id)
                .ToListAsync();
            _db.Pairings.RemoveRange(existingPairings);

            foreach (var pp in parsedPairings)
            {
                playersBySnr.TryGetValue(pp.WhiteSnr, out var whitePlayer);
                playersBySnr.TryGetValue(pp.BlackSnr, out var blackPlayer);

                _db.Pairings.Add(new Pairing
                {
                    RoundId = round.Id,
                    BoardNumber = pp.BoardNumber,
                    WhitePlayerId = whitePlayer?.Id,
                    BlackPlayerId = blackPlayer?.Id,
                    Result = pp.Result
                });
            }

            round.ResultsPublished = parsedPairings.Any(p => !string.IsNullOrEmpty(p.Result));
            await _db.SaveChangesAsync();
        }
    }

    private async Task<Round> GetOrCreateRoundAsync(int tournamentId, int roundNum)
    {
        var round = await _db.Rounds
            .FirstOrDefaultAsync(r => r.TournamentId == tournamentId && r.RoundNumber == roundNum);

        if (round is null)
        {
            round = new Round
            {
                TournamentId = tournamentId,
                RoundNumber = roundNum,
                PairingsPublished = true
            };
            _db.Rounds.Add(round);
            await _db.SaveChangesAsync();
        }

        return round;
    }

    public async Task<string> FetchPageAsync(string baseUrl, string queryParams)
    {
        var separator = baseUrl.Contains('?') ? "&" : "?";
        var url = $"{baseUrl}{separator}{queryParams}";
        return await FetchHtmlAsync(url);
    }

    public async Task<(string Url, string Html)> FetchWithRedirectAsync(string url)
    {
        await RateLimitAsync();
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        var html = await response.Content.ReadAsStringAsync();
        return (finalUrl, html);
    }

    public async Task<string> FetchHtmlAsync(string url)
    {
        await RateLimitAsync();
        _logger.LogDebug("Fetching {Url}", url);
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task RateLimitAsync()
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRequest).TotalMilliseconds;
            if (elapsed < DelayMs)
            {
                await Task.Delay(DelayMs - (int)elapsed);
            }
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
