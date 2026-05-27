using System.Text;
using System.Text.Json;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChessResultsCrawler.Services;

public class CrawlerService
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _gluetunClient;
    private readonly HtmlParserService _parser;
    private readonly AppDbContext _db;
    private readonly ILogger<CrawlerService> _logger;
    private readonly string _gluetunApiUrl;
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static int _requestCount;
    private const int DelayMs = 1500;
    private const int RetryDelayMs = 5000;
    private const int VpnRestartPauseMs = 3000;
    private const int RotateAfterRequests = 20;

    public CrawlerService(HttpClient httpClient, IHttpClientFactory httpClientFactory, HtmlParserService parser, AppDbContext db,
        ILogger<CrawlerService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _gluetunClient = httpClientFactory.CreateClient("Gluetun");
        _parser = parser;
        _db = db;
        _logger = logger;
        _gluetunApiUrl = configuration["Gluetun__ApiUrl"] ?? "http://localhost:8000";
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

            // S-7: SSRF protection – only allow redirects to chess-results.com
            var resolvedUri = new Uri(resolvedUrl);
            if (!resolvedUri.Host.EndsWith("chess-results.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Redirect to unexpected domain: {resolvedUri.Host}");

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

            // Get total rounds + tournament details from art=0 with turdet=YES
            var art0Html = await FetchPageAsync(resolvedUrl, "art=0&turdet=YES");
            var totalRounds = await _parser.ParseTotalRoundsAsync(art0Html);
            if (totalRounds.HasValue)
                tournament.TotalRounds = totalRounds.Value;

            // Parse tournament date and location from turdet details
            var details = await _parser.ParseTournamentDetailsAsync(art0Html);
            if (details.Location is not null)
                tournament.Location = details.Location;
            if (details.DateText is not null)
                tournament.DateText = details.DateText;

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
                case CrawlJobType.PlayerDetails:
                    // Handled by CrawlPlayerDetailsAsync (called directly)
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

    public async Task CrawlPlayerDetailsAsync(string chessResultsId, List<int> playerSnrs)
    {
        var tournament = await _db.Tournaments
            .FirstOrDefaultAsync(t => t.ChessResultsId == chessResultsId);

        if (tournament?.BaseUrl is null)
        {
            _logger.LogWarning("Tournament {Id} not found or has no BaseUrl for player detail crawl", chessResultsId);
            return;
        }

        var baseUrl = tournament.BaseUrl;

        // Load players for this tournament (map Snr -> Player entity)
        var playersBySnr = await _db.Players
            .Where(p => p.TournamentId == tournament.Id)
            .ToDictionaryAsync(p => p.Snr);

        // Load rounds for this tournament (map RoundNumber -> Round entity)
        var roundsByNumber = await _db.Rounds
            .Where(r => r.TournamentId == tournament.Id)
            .ToDictionaryAsync(r => r.RoundNumber);

        foreach (var snr in playerSnrs)
        {
            try
            {
                if (!playersBySnr.TryGetValue(snr, out var player))
                {
                    _logger.LogWarning("Player SNR {Snr} not found in tournament {Id}", snr, chessResultsId);
                    continue;
                }

                var html = await FetchPageAsync(baseUrl, $"art=9&snr={snr}");
                var parsed = await _parser.ParsePlayerDetailPageAsync(html);

                foreach (var pr in parsed)
                {
                    if (!roundsByNumber.TryGetValue(pr.RoundNumber, out var round))
                    {
                        round = new Round
                        {
                            TournamentId = tournament.Id,
                            RoundNumber = pr.RoundNumber,
                            PairingsPublished = true
                        };
                        _db.Rounds.Add(round);
                        await _db.SaveChangesAsync();
                        roundsByNumber[pr.RoundNumber] = round;
                    }

                    var existing = await _db.PlayerResults
                        .FirstOrDefaultAsync(r => r.RoundId == round.Id && r.PlayerId == player.Id);

                    if (existing is not null)
                    {
                        existing.BoardNumber = pr.BoardNumber;
                        existing.Result = pr.Result;
                        existing.OpponentSnr = pr.OpponentSnr;
                        existing.OpponentName = pr.OpponentName;
                        existing.OpponentElo = pr.OpponentElo;
                        existing.Points = pr.Points;
                    }
                    else
                    {
                        _db.PlayerResults.Add(new PlayerResult
                        {
                            RoundId = round.Id,
                            PlayerId = player.Id,
                            BoardNumber = pr.BoardNumber,
                            Result = pr.Result,
                            OpponentSnr = pr.OpponentSnr,
                            OpponentName = pr.OpponentName,
                            OpponentElo = pr.OpponentElo,
                            Points = pr.Points
                        });
                    }
                }

                await _db.SaveChangesAsync();
                _logger.LogInformation("Crawled {Count} results for player SNR {Snr} in tournament {Id}",
                    parsed.Count, snr, chessResultsId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error crawling details for player SNR {Snr} in tournament {Id}",
                    snr, chessResultsId);
            }
        }
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

            // H-9: Wrap delete+insert in transaction for re-crawl safety
            await using var tx = await _db.Database.BeginTransactionAsync();

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
            await tx.CommitAsync();
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

            // H-9: Wrap delete+insert in transaction for re-crawl safety
            await using var tx = await _db.Database.BeginTransactionAsync();

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
            await tx.CommitAsync();
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
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            var html = await response.Content.ReadAsStringAsync();
            return (finalUrl, html);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Fetch failed for {Url}, retrying in {Delay}ms", url, RetryDelayMs);
            await Task.Delay(RetryDelayMs);
            await RateLimitAsync();
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            var html = await response.Content.ReadAsStringAsync();
            return (finalUrl, html);
        }
    }

    public async Task<string> FetchHtmlAsync(string url)
    {
        await RateLimitAsync();
        _logger.LogDebug("Fetching {Url}", url);
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Fetch failed for {Url}, retrying in {Delay}ms", url, RetryDelayMs);
            await Task.Delay(RetryDelayMs);
            await RateLimitAsync();
            _logger.LogDebug("Retrying {Url}", url);
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
    }

    private async Task RotateVpnAsync()
    {
        try
        {
            var statusUrl = $"{_gluetunApiUrl}/v1/vpn/status";
            var stopContent = new StringContent("""{"status":"stopped"}""", Encoding.UTF8, "application/json");
            var startContent = new StringContent("""{"status":"running"}""", Encoding.UTF8, "application/json");

            _logger.LogInformation("Rotating VPN IP...");
            await _gluetunClient.PutAsync(statusUrl, stopContent);
            await Task.Delay(VpnRestartPauseMs);
            await _gluetunClient.PutAsync(statusUrl, startContent);
            _logger.LogInformation("VPN IP rotated");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VPN rotation failed (non-critical)");
        }
    }

    public async Task<List<ParsedPlayerSearchResult>> SearchPlayersAsync(string lastName, string? firstName)
    {
        // Step 1: GET the search page to obtain ASP.NET ViewState
        var url = "https://chess-results.com/SpielerSuche.aspx?lan=0";
        var (resolvedUrl, formHtml) = await FetchWithRedirectAsync(url);

        // SSRF protection: only allow chess-results.com domains
        var resolvedUri = new Uri(resolvedUrl);
        if (!resolvedUri.Host.EndsWith("chess-results.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Redirect to unexpected domain: {resolvedUri.Host}");

        // Step 2: Extract hidden form fields (__VIEWSTATE, __EVENTVALIDATION, __VIEWSTATEGENERATOR)
        var viewState = ExtractHiddenField(formHtml, "__VIEWSTATE");
        var eventValidation = ExtractHiddenField(formHtml, "__EVENTVALIDATION");
        var viewStateGenerator = ExtractHiddenField(formHtml, "__VIEWSTATEGENERATOR");

        // Step 3: POST the search form
        var formData = new Dictionary<string, string>
        {
            ["__VIEWSTATE"] = viewState ?? "",
            ["__EVENTVALIDATION"] = eventValidation ?? "",
            ["__VIEWSTATEGENERATOR"] = viewStateGenerator ?? "",
            ["ctl00$P1$txt_nachname"] = lastName,
            ["ctl00$P1$txt_vorname"] = firstName ?? "",
            ["ctl00$P1$cb_suchen"] = "Suchen"
        };

        await RateLimitAsync();
        var content = new FormUrlEncodedContent(formData);
        var response = await _httpClient.PostAsync(resolvedUrl, content);
        response.EnsureSuccessStatusCode();

        var resultHtml = await response.Content.ReadAsStringAsync();
        var results = await _parser.ParsePlayerSearchAsync(resultHtml);

        // Deduplicate: SpielerSuche returns one row per tournament per player
        // Prefer entries with a real ChessResultsId (not "0" or empty)
        var deduplicated = results
            .GroupBy(r => r.Name)
            .Select(g => g.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ChessResultsId) && r.ChessResultsId != "0") ?? g.First())
            .Take(50)
            .ToList();

        return deduplicated;
    }

    public async Task<List<ParsedPlayerTournament>> SearchPlayerTournamentsAsync(string lastName, string? firstName)
    {
        // Same POST flow as SearchPlayersAsync
        var url = "https://chess-results.com/SpielerSuche.aspx?lan=0";
        var (resolvedUrl, formHtml) = await FetchWithRedirectAsync(url);

        var resolvedUri = new Uri(resolvedUrl);
        if (!resolvedUri.Host.EndsWith("chess-results.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Redirect to unexpected domain: {resolvedUri.Host}");

        var viewState = ExtractHiddenField(formHtml, "__VIEWSTATE");
        var eventValidation = ExtractHiddenField(formHtml, "__EVENTVALIDATION");
        var viewStateGenerator = ExtractHiddenField(formHtml, "__VIEWSTATEGENERATOR");

        var formData = new Dictionary<string, string>
        {
            ["__VIEWSTATE"] = viewState ?? "",
            ["__EVENTVALIDATION"] = eventValidation ?? "",
            ["__VIEWSTATEGENERATOR"] = viewStateGenerator ?? "",
            ["ctl00$P1$txt_nachname"] = lastName,
            ["ctl00$P1$txt_vorname"] = firstName ?? "",
            ["ctl00$P1$cb_suchen"] = "Suchen"
        };

        await RateLimitAsync();
        var content = new FormUrlEncodedContent(formData);
        var response = await _httpClient.PostAsync(resolvedUrl, content);
        response.EnsureSuccessStatusCode();

        var resultHtml = await response.Content.ReadAsStringAsync();
        var results = await _parser.ParsePlayerTournamentsAsync(resultHtml);

        // Deduplicate and limit
        return results
            .GroupBy(r => r.TournamentId)
            .Select(g => g.First())
            .Take(50)
            .ToList();
    }

    private static string? ExtractHiddenField(string html, string fieldName)
    {
        // Match: name="fieldName" ... value="..."
        var pattern = $"name=\"{fieldName}\"[^>]*value=\"([^\"]*)\"";
        var match = System.Text.RegularExpressions.Regex.Match(html, pattern);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private async Task RateLimitAsync()
    {
        if (!await _rateLimiter.WaitAsync(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("Rate limiter acquisition timed out after 60 seconds.");
        try
        {
            // Rotate VPN IP every N requests (keep semaphore held during rotation)
            _requestCount++;
            if (_requestCount >= RotateAfterRequests)
            {
                _requestCount = 0;
                await RotateVpnAsync();
            }

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
