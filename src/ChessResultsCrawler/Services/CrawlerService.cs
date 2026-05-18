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
        var html = await FetchPageAsync(baseUrl, "art=16&zeilen=99999");
        _logger.LogInformation("Fetched art=15 page, HTML length: {Length}", html.Length);
        var parsedPlayers = await _parser.ParsePlayerListAsync(html);
        _logger.LogInformation("Parsed {Count} players from HTML", parsedPlayers.Count);

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
            // If no round links found, try rounds 1..TotalRounds
            availableRounds = Enumerable.Range(1, tournament.TotalRounds).ToList();
        }

        var teams = await _db.Teams
            .Where(t => t.TournamentId == tournament.Id)
            .ToDictionaryAsync(t => t.Name);

        foreach (var roundNum in availableRounds)
        {
            var round = await _db.Rounds
                .FirstOrDefaultAsync(r => r.TournamentId == tournament.Id && r.RoundNumber == roundNum);

            if (round is null)
            {
                round = new Round
                {
                    TournamentId = tournament.Id,
                    RoundNumber = roundNum,
                    PairingsPublished = true
                };
                _db.Rounds.Add(round);
                await _db.SaveChangesAsync();
            }

            var roundHtml = await FetchPageAsync(baseUrl, $"art=2&rd={roundNum}");
            var parsedPairings = await _parser.ParseTeamPairingsAsync(roundHtml);

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

                var pairing = new TeamPairing
                {
                    RoundId = round.Id,
                    MatchNumber = pp.MatchNumber,
                    HomeTeamId = homeTeam.Id,
                    AwayTeamId = awayTeam.Id,
                    HomeScore = pp.HomeScore,
                    AwayScore = pp.AwayScore
                };
                _db.TeamPairings.Add(pairing);
            }

            round.ResultsPublished = parsedPairings.Any(p => p.HomeScore.HasValue);
            await _db.SaveChangesAsync();
        }
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
