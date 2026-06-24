using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace ChessResultsCrawler.Services;

public class CrawlerService
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _gluetunClient;
    private readonly HtmlParserService _parser;
    private readonly AppDbContext _db;
    private readonly ILogger<CrawlerService> _logger;
    private readonly string _gluetunApiUrl;
    private readonly int _retryDelayMs;
    private readonly int _crawlMaxAttempts;
    private readonly int _crawlRetryBackoffSeconds;
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static int _requestCount;
    private const int DelayMs = 1500;
    private const int VpnRestartPauseMs = 3000;
    private const int RotateAfterRequests = 20;
    private const int DefaultRetryDelayMs = 5000;
    // Re-Queue eines kompletten Crawls bei Verbindungsfehlern (z.B. VPN-Tunnel kurz weg nach
    // Rotation/Deploy): bis zu CrawlMaxAttempts Anläufe mit gestuftem Backoff, statt sofort Failed.
    private const int DefaultCrawlMaxAttempts = 4;
    private static readonly int[] DefaultCrawlRetryBackoffSeconds = { 15, 30, 45 };

    public CrawlerService(HttpClient httpClient, IHttpClientFactory httpClientFactory, HtmlParserService parser, AppDbContext db,
        ILogger<CrawlerService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _gluetunClient = httpClientFactory.CreateClient("Gluetun");
        _parser = parser;
        _db = db;
        _logger = logger;
        _gluetunApiUrl = configuration["Gluetun:ApiUrl"] ?? configuration["Gluetun__ApiUrl"] ?? "http://localhost:8000";
        _retryDelayMs = configuration.GetValue("Crawler:RetryDelayMs", DefaultRetryDelayMs);
        _crawlMaxAttempts = Math.Max(1, configuration.GetValue("Crawler:CrawlMaxAttempts", DefaultCrawlMaxAttempts));
        // -1 ⇒ gestufte Default-Backoffs; >=0 ⇒ flacher Wert (v.a. für Tests, um schnell zu sein).
        _crawlRetryBackoffSeconds = configuration.GetValue("Crawler:CrawlRetryBackoffSeconds", -1);
    }

    /// <summary>Backoff vor dem nächsten Crawl-Anlauf (1-basierter <paramref name="attempt"/>).</summary>
    private TimeSpan BackoffFor(int attempt)
    {
        if (_crawlRetryBackoffSeconds >= 0)
            return TimeSpan.FromSeconds(_crawlRetryBackoffSeconds);
        var idx = Math.Min(attempt - 1, DefaultCrawlRetryBackoffSeconds.Length - 1);
        return TimeSpan.FromSeconds(DefaultCrawlRetryBackoffSeconds[idx]);
    }

    public async Task<CrawlJob> ExecuteCrawlAsync(CrawlJob job, CancellationToken ct = default)
    {
        // Gesamten Crawl-Lebenszyklus mit dem Domain-Tag "crawl" markieren, damit Start-/Erfolgs-/
        // Fehler-Logs (inkl. der CrawlRequest-Zeilen aus FetchHtml/FetchWithRedirect) zentral in
        // Kibana über das ECS-`tags`-Feld filterbar sind.
        using var _ = LogContext.PushProperty("LogTags", "crawl");

        job.Status = CrawlJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        _logger.LogInformation("Starting crawl {JobType} for {ChessResultsId}", job.JobType, job.ChessResultsId);
        await _db.SaveChangesAsync(ct);

        // Re-Queue-Schleife: ein Crawl, der NUR auf Verbindungsebene scheitert (Tunnel kurz weg
        // nach VPN-Rotation/Deploy), wird gestuft erneut versucht statt sofort als Failed markiert.
        // Das Upsert ist idempotent (find-or-create + RemoveRange/Insert pro Runde in Transaktion),
        // ein erneuter Anlauf ist daher gefahrlos.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // Resolve tournament base URL and SNode
                var baseUrl = $"https://chess-results.com/tnr{job.ChessResultsId}.aspx?lan=0";
                var (resolvedUrl, html) = await FetchWithRedirectAsync(baseUrl, ct);

                // S-7: SSRF protection – only allow redirects to chess-results.com
                EnsureChessResultsHost(resolvedUrl);

                var sNode = HtmlParserService.ExtractSNode(resolvedUrl);

                // Find or create tournament
                var tournament = await _db.Tournaments
                    .FirstOrDefaultAsync(t => t.ChessResultsId == job.ChessResultsId, ct);

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
                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    tournament.BaseUrl = resolvedUrl;
                    tournament.SNode = sNode;
                    tournament.UpdatedAt = DateTime.UtcNow;
                }

                job.TournamentId = tournament.Id;

                // Get total rounds + tournament details from art=0 with turdet=YES
                var art0Html = await FetchPageAsync(resolvedUrl, "art=0&turdet=YES", ct);
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
                        await CrawlPlayersAsync(tournament, resolvedUrl, ct);
                        await CrawlAllPairingsAsync(tournament, resolvedUrl, ct);
                        break;
                    case CrawlJobType.PlayersOnly:
                        await CrawlPlayersAsync(tournament, resolvedUrl, ct);
                        break;
                    case CrawlJobType.PairingsOnly:
                        await CrawlAllPairingsAsync(tournament, resolvedUrl, ct);
                        break;
                    case CrawlJobType.CheckNewRounds:
                        // Handled by RoundDetectionService
                        break;
                    case CrawlJobType.PlayerDetails:
                        // Handled by CrawlPlayerDetailsAsync (called directly)
                        break;
                }

                await _db.SaveChangesAsync(ct);
                job.Status = CrawlJobStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("Crawl {JobType} completed for {ChessResultsId}", job.JobType, job.ChessResultsId);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogWarning("Crawl cancelled for {ChessResultsId}", job.ChessResultsId);
                job.Status = CrawlJobStatus.Failed;
                job.ErrorMessage = "Cancelled";
                job.CompletedAt = DateTime.UtcNow;
                break;
            }
            catch (Exception ex) when (IsTransientConnectionError(ex) && attempt < _crawlMaxAttempts)
            {
                var backoff = BackoffFor(attempt);
                _logger.LogWarning(ex,
                    "Crawl {ChessResultsId}: Verbindungsfehler (Versuch {Attempt}/{Max}), Re-Queue in {Delay}s",
                    job.ChessResultsId, attempt, _crawlMaxAttempts, backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    job.Status = CrawlJobStatus.Failed;
                    job.ErrorMessage = "Cancelled";
                    job.CompletedAt = DateTime.UtcNow;
                    break;
                }
                // nächster Schleifendurchlauf = erneuter Anlauf
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Crawl failed for {ChessResultsId}", job.ChessResultsId);
                job.Status = CrawlJobStatus.Failed;
                var msg = ex.Message ?? "Unknown error";
                job.ErrorMessage = msg[..Math.Min(msg.Length, 2000)];
                job.CompletedAt = DateTime.UtcNow;
                break;
            }
        }

        // Finalen Status IMMER persistieren — auch bei Cancellation. Mit dem (bereits gecancelten)
        // ct würde dieser Save erneut werfen → der Job bliebe für immer auf Running stehen.
        await _db.SaveChangesAsync(CancellationToken.None);
        return job;
    }

    /// <summary>
    /// Baut die Name→Team-Map für das Upsert tolerant gegen doppelte Teamnamen: bei einer
    /// Dublette gewinnt das Team mit der kleinsten <see cref="Team.Snr"/> (stabil, deterministisch).
    /// Verhindert die <c>ToDictionary</c>-Exception bei doppelten Namen, die sonst den
    /// gesamten Spieler-Crawl scheitern lässt.
    /// </summary>
    public static Dictionary<string, Team> BuildTeamNameMap(IEnumerable<Team> teams)
    {
        var map = new Dictionary<string, Team>();
        foreach (var team in teams.OrderBy(t => t.Snr))
            map.TryAdd(team.Name, team);
        return map;
    }

    public async Task CrawlPlayerDetailsAsync(string chessResultsId, List<int> playerSnrs, CancellationToken ct = default)
    {
        var tournament = await _db.Tournaments
            .FirstOrDefaultAsync(t => t.ChessResultsId == chessResultsId, ct);

        if (tournament?.BaseUrl is null)
        {
            _logger.LogWarning("Tournament {Id} not found or has no BaseUrl for player detail crawl", chessResultsId);
            return;
        }

        var baseUrl = tournament.BaseUrl;

        // Load players for this tournament (map Snr -> Player entity)
        var playersBySnr = await _db.Players
            .Where(p => p.TournamentId == tournament.Id)
            .ToDictionaryAsync(p => p.Snr, ct);

        // Load rounds for this tournament (map RoundNumber -> Round entity)
        var roundsByNumber = await _db.Rounds
            .Where(r => r.TournamentId == tournament.Id)
            .ToDictionaryAsync(r => r.RoundNumber, ct);

        foreach (var snr in playerSnrs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!playersBySnr.TryGetValue(snr, out var player))
                {
                    _logger.LogWarning("Player SNR {Snr} not found in tournament {Id}", snr, chessResultsId);
                    continue;
                }

                var html = await FetchPageAsync(baseUrl, $"art=9&snr={snr}", ct);
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
                        await _db.SaveChangesAsync(ct);
                        roundsByNumber[pr.RoundNumber] = round;
                    }

                    var existing = await _db.PlayerResults
                        .FirstOrDefaultAsync(r => r.RoundId == round.Id && r.PlayerId == player.Id, ct);

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

                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Crawled {Count} results for player SNR {Snr} in tournament {Id}",
                    parsed.Count, snr, chessResultsId);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error crawling details for player SNR {Snr} in tournament {Id}",
                    snr, chessResultsId);
            }
        }
    }

    private async Task CrawlPlayersAsync(Tournament tournament, string baseUrl, CancellationToken ct)
    {
        _logger.LogInformation("Crawling players for tournament {Id}", tournament.ChessResultsId);

        // Try art=16 (team tournaments full list), fall back to art=0 (individual tournaments)
        var html = await FetchPageAsync(baseUrl, "art=16&zeilen=99999", ct);
        var parsedPlayers = await _parser.ParsePlayerListAsync(html);
        _logger.LogInformation("art=16: parsed {Count} players", parsedPlayers.Count);

        if (parsedPlayers.Count == 0)
        {
            html = await FetchPageAsync(baseUrl, "art=0&zeilen=99999", ct);
            parsedPlayers = await _parser.ParsePlayerListAsync(html);
            _logger.LogInformation("art=0: parsed {Count} players", parsedPlayers.Count);
        }

        // Load existing teams for name-matching. Tolerant gegen doppelte/leere Teamnamen
        // (Tippfehler oder echte Dubletten in den Quelldaten) — ToDictionary würde sonst
        // mit einer Exception den GANZEN Spieler-Crawl killen.
        var existingTeams = BuildTeamNameMap(
            await _db.Teams.Where(t => t.TournamentId == tournament.Id).ToListAsync(ct));

        var existingPlayers = await _db.Players
            .Where(p => p.TournamentId == tournament.Id)
            .ToDictionaryAsync(p => p.Snr, ct);

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

        await _db.SaveChangesAsync(ct);
    }

    private async Task CrawlAllPairingsAsync(Tournament tournament, string baseUrl, CancellationToken ct)
    {
        _logger.LogInformation("Crawling pairings for tournament {Id}", tournament.ChessResultsId);

        // First discover available rounds from art=2
        var art2Html = await FetchPageAsync(baseUrl, "art=2", ct);
        var availableRounds = await _parser.ParseAvailableRoundsAsync(art2Html, tournament.TotalRounds);

        if (availableRounds.Count == 0 && tournament.TotalRounds > 0)
        {
            availableRounds = Enumerable.Range(1, tournament.TotalRounds).ToList();
        }

        // Detect tournament type from first round page
        var isTeam = await _parser.IsTeamPairingsPageAsync(art2Html);
        _logger.LogInformation("Tournament {Id} pairings type: {Type}", tournament.ChessResultsId, isTeam ? "Team" : "Individual");

        if (isTeam)
        {
            await CrawlTeamPairingsAsync(tournament, baseUrl, availableRounds, ct);
        }
        else
        {
            await CrawlIndividualPairingsAsync(tournament, baseUrl, availableRounds, ct);
        }
    }

    private async Task CrawlTeamPairingsAsync(Tournament tournament, string baseUrl, List<int> availableRounds, CancellationToken ct)
    {
        var teams = await _db.Teams
            .Where(t => t.TournamentId == tournament.Id)
            .ToDictionaryAsync(t => t.Name, ct);

        foreach (var roundNum in availableRounds)
        {
            ct.ThrowIfCancellationRequested();
            var round = await GetOrCreateRoundAsync(tournament.Id, roundNum, ct);

            var roundHtml = await FetchPageAsync(baseUrl, $"art=2&rd={roundNum}", ct);
            var parsedPairings = await _parser.ParseTeamPairingsAsync(roundHtml);
            _logger.LogInformation("Round {Round}: parsed {Count} team pairings", roundNum, parsedPairings.Count);

            // H-9: Wrap delete+insert in transaction for re-crawl safety
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var existingPairings = await _db.TeamPairings
                .Where(tp => tp.RoundId == round.Id)
                .ToListAsync(ct);
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
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    private async Task CrawlIndividualPairingsAsync(Tournament tournament, string baseUrl, List<int> availableRounds, CancellationToken ct)
    {
        var playersBySnr = await _db.Players
            .Where(p => p.TournamentId == tournament.Id)
            .ToDictionaryAsync(p => p.Snr, ct);

        foreach (var roundNum in availableRounds)
        {
            ct.ThrowIfCancellationRequested();
            var round = await GetOrCreateRoundAsync(tournament.Id, roundNum, ct);

            var roundHtml = await FetchPageAsync(baseUrl, $"art=2&rd={roundNum}", ct);
            var parsedPairings = await _parser.ParseIndividualPairingsAsync(roundHtml);
            _logger.LogInformation("Round {Round}: parsed {Count} individual pairings", roundNum, parsedPairings.Count);

            // H-9: Wrap delete+insert in transaction for re-crawl safety
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var existingPairings = await _db.Pairings
                .Where(p => p.RoundId == round.Id)
                .ToListAsync(ct);
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
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    private async Task<Round> GetOrCreateRoundAsync(int tournamentId, int roundNum, CancellationToken ct)
    {
        var round = await _db.Rounds
            .FirstOrDefaultAsync(r => r.TournamentId == tournamentId && r.RoundNumber == roundNum, ct);

        if (round is null)
        {
            round = new Round
            {
                TournamentId = tournamentId,
                RoundNumber = roundNum,
                PairingsPublished = true
            };
            _db.Rounds.Add(round);
            await _db.SaveChangesAsync(ct);
        }

        return round;
    }

    public async Task<string> FetchPageAsync(string baseUrl, string queryParams, CancellationToken ct = default)
    {
        var separator = baseUrl.Contains('?') ? "&" : "?";
        var url = $"{baseUrl}{separator}{queryParams}";
        return await FetchHtmlAsync(url, ct);
    }

    // SSRF-Schutz: nach (automatisch gefolgten) Redirects muss der finale Host
    // weiterhin chess-results.com sein. Exakter Vergleich, damit z.B.
    // "evilchess-results.com" oder "chess-results.com.attacker.tld" abgewiesen wird.
    private static void EnsureChessResultsHost(string resolvedUrl)
    {
        Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri);
        var host = uri?.Host ?? string.Empty;
        if (!(host.Equals("chess-results.com", StringComparison.OrdinalIgnoreCase)
              || host.EndsWith(".chess-results.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Redirect to unexpected domain: {resolvedUrl}");
    }

    public async Task<(string Url, string Html)> FetchWithRedirectAsync(string url, CancellationToken ct = default)
    {
        await RateLimitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, html, response.IsSuccessStatusCode, null, false);
            EnsureChessResultsHost(response.RequestMessage?.RequestUri?.ToString() ?? url);
            response.EnsureSuccessStatusCode();
            return (finalUrl, html);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            LogCrawlRequest(url, null, sw.ElapsedMilliseconds, null, false, ex.Message, false);
            _logger.LogWarning(ex, "Fetch failed for {Url}, retrying in {Delay}ms", url, _retryDelayMs);
            await Task.Delay(_retryDelayMs, ct);
            await RateLimitAsync(ct);
            sw = Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(url, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, html, response.IsSuccessStatusCode, null, true);
            EnsureChessResultsHost(response.RequestMessage?.RequestUri?.ToString() ?? url);
            response.EnsureSuccessStatusCode();
            return (finalUrl, html);
        }
    }

    public async Task<string> FetchHtmlAsync(string url, CancellationToken ct = default)
    {
        await RateLimitAsync(ct);
        _logger.LogDebug("Fetching {Url}", url);
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, body, response.IsSuccessStatusCode, null, false);
            EnsureChessResultsHost(response.RequestMessage?.RequestUri?.ToString() ?? url);
            response.EnsureSuccessStatusCode();
            return body;
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            LogCrawlRequest(url, null, sw.ElapsedMilliseconds, null, false, ex.Message, false);
            _logger.LogWarning(ex, "Fetch failed for {Url}, retrying in {Delay}ms", url, _retryDelayMs);
            await Task.Delay(_retryDelayMs, ct);
            await RateLimitAsync(ct);
            _logger.LogDebug("Retrying {Url}", url);
            sw = Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, body, response.IsSuccessStatusCode, null, true);
            EnsureChessResultsHost(response.RequestMessage?.RequestUri?.ToString() ?? url);
            response.EnsureSuccessStatusCode();
            return body;
        }
    }

    private async Task RotateVpnAsync(CancellationToken ct)
    {
        try
        {
            var statusUrl = $"{_gluetunApiUrl}/v1/vpn/status";
            var stopContent = new StringContent("""{"status":"stopped"}""", Encoding.UTF8, "application/json");
            var startContent = new StringContent("""{"status":"running"}""", Encoding.UTF8, "application/json");

            _logger.LogInformation("Rotating VPN IP...");
            await _gluetunClient.PutAsync(statusUrl, stopContent, ct);
            await Task.Delay(VpnRestartPauseMs, ct);
            await _gluetunClient.PutAsync(statusUrl, startContent, ct);
            // Nach der Rotation den Rate-Limiter-Zeitstempel zuruecksetzen, damit die
            // erste Anfrage ueber die neue Verbindung den vollen DelayMs-Abstand abwartet.
            _lastRequest = DateTime.UtcNow;

            // Neue Public-IP ermitteln und mitloggen (zur Korrelation in ES/Kibana).
            var newIp = await TryGetPublicIpAsync(ct);
            if (newIp is not null)
                _logger.LogInformation("VPN IP rotated → {NewIp}", newIp);
            else
                _logger.LogInformation("VPN IP rotated (neue IP nicht ermittelbar)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VPN rotation failed (non-critical)");
        }
    }

    /// <summary>
    /// Fragt die aktuelle Public-IP beim gluetun-Control-Server ab (best-effort, non-critical).
    /// gluetun braucht nach dem Reconnect kurz, bis die neue IP ermittelt ist → kurzes Polling.
    /// </summary>
    private async Task<string?> TryGetPublicIpAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await Task.Delay(1000, ct);
                var json = await _gluetunClient.GetStringAsync($"{_gluetunApiUrl}/v1/publicip/ip", ct);
                var ip = ParsePublicIp(json);
                if (!string.IsNullOrWhiteSpace(ip)) return ip;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "publicip query attempt {Attempt} failed", attempt + 1);
            }
        }
        return null;
    }

    /// <summary>Extrahiert die <c>public_ip</c> aus der gluetun-Antwort von <c>/v1/publicip/ip</c>.</summary>
    public static string? ParsePublicIp(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("public_ip", out var ipEl)
                && ipEl.ValueKind == JsonValueKind.String)
            {
                var ip = ipEl.GetString();
                return string.IsNullOrWhiteSpace(ip) ? null : ip;
            }
        }
        catch (JsonException) { /* keine gültige JSON → null */ }
        return null;
    }

    /// <summary>
    /// Erkennt reine Verbindungs-/Transportfehler (kein HTTP-Status erhalten) — typisch wenn der
    /// VPN-Tunnel kurz weg ist: "Resource temporarily unavailable", Socket-Reset/Refused, Timeout.
    /// Solche Fehler sind transient und rechtfertigen einen erneuten Crawl-Anlauf. HTTP-Fehler MIT
    /// Status (404/500…) gelten NICHT als transient, damit echte Serverfehler nicht endlos laufen.
    /// </summary>
    public static bool IsTransientConnectionError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException hre:
                    if (hre.StatusCode is null) return true;
                    break;
                case System.Net.Sockets.SocketException:
                case TimeoutException:
                case TaskCanceledException:
                case OperationCanceledException:
                    return true;
            }

            var m = e.Message;
            if (!string.IsNullOrEmpty(m) &&
                (m.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
                 || m.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                 || m.Contains("connection reset", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    public async Task<List<ParsedPlayerSearchResult>> SearchPlayersAsync(string lastName, string? firstName, CancellationToken ct = default)
    {
        // Step 1: GET the search page to obtain ASP.NET ViewState
        var url = "https://chess-results.com/SpielerSuche.aspx?lan=0";
        var (resolvedUrl, formHtml) = await FetchWithRedirectAsync(url, ct);

        // SSRF protection: only allow chess-results.com domains
        EnsureChessResultsHost(resolvedUrl);

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

        await RateLimitAsync(ct);
        var content = new FormUrlEncodedContent(formData);
        var response = await _httpClient.PostAsync(resolvedUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var resultHtml = await response.Content.ReadAsStringAsync(ct);
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

    public async Task<List<ParsedPlayerTournament>> SearchPlayerTournamentsAsync(string lastName, string? firstName, CancellationToken ct = default)
    {
        // Same POST flow as SearchPlayersAsync
        var url = "https://chess-results.com/SpielerSuche.aspx?lan=0";
        var (resolvedUrl, formHtml) = await FetchWithRedirectAsync(url, ct);

        EnsureChessResultsHost(resolvedUrl);

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

        await RateLimitAsync(ct);
        var content = new FormUrlEncodedContent(formData);
        var response = await _httpClient.PostAsync(resolvedUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var resultHtml = await response.Content.ReadAsStringAsync(ct);
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

    private void LogCrawlRequest(string url, int? statusCode, long durationMs, string? responseBody, bool success, string? error, bool isRetry)
    {
        // Bewusst KEIN Response-Body mehr ins Log: das gecrawlte HTML (bis 500 KB/Request) blähte
        // den Elasticsearch-Data-Stream massiv auf und enthielt ungefilterte Spieler-PII. Nur noch
        // die Größe (CrawlResponseSize) festhalten — der Inhalt ist ohnehin in der DB.
        // "crawl"-Tag auch hier, damit jeder einzelne Fetch-gegen-chess-results.com (unabhängig vom
        // Aufrufer, z.B. Spielersuche) zentral filterbar ist.
        using var _ = LogContext.PushProperty("LogTags", "crawl");
        _logger.LogInformation(
            "CrawlRequest {CrawlUrl} Status={CrawlStatusCode} Duration={CrawlDurationMs}ms " +
            "Size={CrawlResponseSize} Success={CrawlSuccess} IsRetry={CrawlIsRetry} Error={CrawlError}",
            url.Length > 2000 ? url[..2000] : url,
            statusCode, durationMs, responseBody?.Length,
            success, isRetry, error);
    }

    private async Task RateLimitAsync(CancellationToken ct = default)
    {
        if (!await _rateLimiter.WaitAsync(TimeSpan.FromSeconds(60), ct))
            throw new TimeoutException("Rate limiter acquisition timed out after 60 seconds.");
        try
        {
            // Rotate VPN IP every N requests (keep semaphore held during rotation)
            _requestCount++;
            if (_requestCount >= RotateAfterRequests)
            {
                _requestCount = 0;
                await RotateVpnAsync(ct);
            }

            var elapsed = (DateTime.UtcNow - _lastRequest).TotalMilliseconds;
            if (elapsed < DelayMs)
            {
                await Task.Delay(DelayMs - (int)elapsed, ct);
            }
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
