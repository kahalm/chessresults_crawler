using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly long _maxResponseBytes;
    private readonly int _rotateAfterRequests;
    private readonly int _vpnRestartPauseMs;
    private readonly int _minDelayMs;
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static int _requestCount;
    private const int DefaultMinDelayMs = 1500;
    private const int DefaultVpnRestartPauseMs = 3000;
    private const int DefaultRotateAfterRequests = 20;
    private const int DefaultRetryDelayMs = 5000;
    // Re-Queue eines kompletten Crawls bei Verbindungsfehlern (z.B. VPN-Tunnel kurz weg nach
    // Rotation/Deploy): bis zu CrawlMaxAttempts Anläufe mit gestuftem Backoff, statt sofort Failed.
    private const int DefaultCrawlMaxAttempts = 4;
    private static readonly int[] DefaultCrawlRetryBackoffSeconds = { 15, 30, 45 };
    // Defensives Obergrenze für die Größe einer chess-results.com-Antwort. Listen werden mit
    // zeilen=99999 geholt; ein bösartiger/fehlerhafter Server könnte beliebig große Bodies liefern
    // und den Heap sprengen. Großzügig (32 MB), aber endlich — sauberer Abbruch statt OOM.
    private const long DefaultMaxResponseBytes = 32L * 1024 * 1024;

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
        _maxResponseBytes = Math.Max(1024, configuration.GetValue("Crawler:MaxResponseBytes", DefaultMaxResponseBytes));
        _rotateAfterRequests = Math.Max(1, configuration.GetValue("Crawler:RotateAfterRequests", DefaultRotateAfterRequests));
        _vpnRestartPauseMs = Math.Max(0, configuration.GetValue("Crawler:VpnRestartPauseMs", DefaultVpnRestartPauseMs));
        _minDelayMs = Math.Max(0, configuration.GetValue("Crawler:MinDelayMs", DefaultMinDelayMs));
    }

    /// <summary>
    /// Liest den Antwort-Body als UTF-8-String, bricht aber bei Überschreiten von
    /// <see cref="_maxResponseBytes"/> sauber mit <see cref="InvalidOperationException"/> ab —
    /// schützt vor unbegrenzten Bodies (Heap-/OOM-Risiko) statt blind zu puffern.
    /// </summary>
    private async Task<string> ReadBodyBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
        {
            if (buffer.Length + read > _maxResponseBytes)
                throw new InvalidOperationException(
                    $"Response body exceeds maximum allowed size of {_maxResponseBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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

    private static readonly string[] ByeMarkers = { "spielfrei", "bye", "freilos" };

    /// <summary>
    /// Erkennt einen "Spielfrei"/Bye/Freilos-Gegner (case-insensitive, getrimmt). Solche Einträge
    /// sind kein echtes Team und kein Fehler — sie dürfen keinen Warn-Alert auslösen.
    /// </summary>
    public static bool IsByeOpponent(string? teamName)
    {
        if (string.IsNullOrWhiteSpace(teamName))
            return false;
        var normalized = teamName.Trim();
        foreach (var marker in ByeMarkers)
            if (normalized.Equals(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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

        // Team- und Spieler-Upsert atomar: bei einem Fehler mitten im Lauf bleiben sonst
        // teilweise neu angelegte Teams (mit verbrauchten Snr) ohne die zugehörigen Spieler zurück.
        // InMemory kennt keine echten Transaktionen → nur bei relationalem Provider klammern.
        IDbContextTransaction? tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        await using var _ = tx;

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
        if (tx is not null)
            await tx.CommitAsync(ct);
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
                    // "Spielfrei"/Freilos/Bye ist KEIN Fehler: bei ungerader Teamzahl bekommt ein
                    // Team eine freie Runde, der Gegner existiert dann nicht als echtes Team.
                    // Solche Paarungen nur informativ loggen, damit sie keinen warn_spike-Alert treiben.
                    if (IsByeOpponent(pp.HomeTeamName) || IsByeOpponent(pp.AwayTeamName))
                        _logger.LogInformation("Bye/spielfrei pairing skipped: {Home} vs {Away}", pp.HomeTeamName, pp.AwayTeamName);
                    else
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

    /// <summary>Ein zulässiges Request-Ziel muss https UND ein chess-results.com-Host sein.</summary>
    private static void EnsureAllowedTarget(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Refusing non-https target: {url}");
        EnsureChessResultsHost(url.ToString());
    }

    private static bool IsRedirectStatus(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Obergrenze der manuell gefolgten Redirect-Hops (Schleifen-/Kettenschutz).</summary>
    private const int MaxRedirectHops = 10;

    /// <summary>
    /// Führt einen Request aus und folgt Redirects MANUELL — jeder Hop wird VOR dem Absenden gegen
    /// https + chess-results.com geprüft (<see cref="EnsureAllowedTarget"/>). Ersetzt das
    /// automatische Redirect-Folgen des HttpClient (in Program.cs via <c>AllowAutoRedirect=false</c>
    /// abgeschaltet), das eine Kette blind bis zu 50 Hops folgte und erst die finale URL prüfte —
    /// der Outbound-Request an einen internen Host feuert damit gar nicht erst. Deckt GET wie POST
    /// (inkl. der POST-Antwort der Spielersuche) ab. Der Body wird bewusst erst beim FINALEN
    /// (nicht-Redirect-)Response gelesen. <paramref name="contentFactory"/> baut den POST-Body je
    /// Hop neu (eine HttpRequestMessage/Content ist nur einmal sendbar).
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpMethod method, Uri url, Func<HttpContent>? contentFactory, CancellationToken ct)
    {
        EnsureAllowedTarget(url);
        var currentMethod = method;
        var currentUrl = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(currentMethod, currentUrl);
            if (currentMethod == HttpMethod.Post) request.Content = contentFactory?.Invoke();
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            var status = response.StatusCode;
            var location = response.Headers.Location;
            if (!IsRedirectStatus(status) || location == null)
                return response;   // finale (nicht-Redirect-)Antwort → Aufrufer liest den Body

            response.Dispose();     // Redirect-Antwort: Body verwerfen, nicht herunterladen
            if (hop >= MaxRedirectHops)
                throw new InvalidOperationException($"Too many redirects (>{MaxRedirectHops}) for {url}");

            // Nächsten Hop bestimmen (relative Location gegen die aktuelle URL auflösen) + PRÜFEN,
            // BEVOR der nächste Request rausgeht.
            var next = new Uri(currentUrl, location);
            EnsureAllowedTarget(next);
            // 301/302/303 → als GET fortsetzen (Browser-/ASP.NET-Verhalten); 307/308 erhalten Methode+Body.
            if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther)
                currentMethod = HttpMethod.Get;
            currentUrl = next;
        }
    }

    public async Task<(string Url, string Html)> FetchWithRedirectAsync(string url, CancellationToken ct = default)
    {
        await RateLimitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            // Redirects werden manuell + je Hop geprüft gefolgt (SSRF-Schutz vor dem Request).
            var response = await SendFollowingRedirectsAsync(HttpMethod.Get, new Uri(url), null, ct);
            var html = await ReadBodyBoundedAsync(response, ct);
            sw.Stop();
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, html, response.IsSuccessStatusCode, null, false);
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
            var response = await SendFollowingRedirectsAsync(HttpMethod.Get, new Uri(url), null, ct);
            var html = await ReadBodyBoundedAsync(response, ct);
            sw.Stop();
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, html, response.IsSuccessStatusCode, null, true);
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
            var response = await SendFollowingRedirectsAsync(HttpMethod.Get, new Uri(url), null, ct);
            var body = await ReadBodyBoundedAsync(response, ct);
            sw.Stop();
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, body, response.IsSuccessStatusCode, null, false);
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
            var response = await SendFollowingRedirectsAsync(HttpMethod.Get, new Uri(url), null, ct);
            var body = await ReadBodyBoundedAsync(response, ct);
            sw.Stop();
            LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, body, response.IsSuccessStatusCode, null, true);
            response.EnsureSuccessStatusCode();
            return body;
        }
    }

    /// <summary>
    /// Startet den VPN-Tunnel neu (stop→pause→start). Läuft UNTER dem Rate-Limiter-Lock, weil der
    /// Tunnel dabei kurz unten ist und in dieser Phase kein Crawl-Request rausgehen darf. Bewusst
    /// KURZ gehalten: die rein informative Public-IP-Ermittlung (bis zu 5 s Polling) wird detached
    /// außerhalb des Locks geloggt, damit wartende Crawls nicht zusätzlich blockiert werden.
    /// </summary>
    private async Task RestartVpnTunnelAsync(CancellationToken ct)
    {
        try
        {
            var statusUrl = $"{_gluetunApiUrl}/v1/vpn/status";
            var stopContent = new StringContent("""{"status":"stopped"}""", Encoding.UTF8, "application/json");
            var startContent = new StringContent("""{"status":"running"}""", Encoding.UTF8, "application/json");

            _logger.LogInformation("Rotating VPN IP...");
            await _gluetunClient.PutAsync(statusUrl, stopContent, ct);
            await Task.Delay(_vpnRestartPauseMs, ct);
            await _gluetunClient.PutAsync(statusUrl, startContent, ct);
            // Nach der Rotation den Rate-Limiter-Zeitstempel zuruecksetzen, damit die
            // erste Anfrage ueber die neue Verbindung den vollen DelayMs-Abstand abwartet.
            _lastRequest = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VPN rotation failed (non-critical)");
            return;
        }

        // Neue Public-IP NICHT mehr im Lock ermitteln (kostete bis zu 5 s Blockade aller Crawls).
        // Detached best-effort loggen, sobald gluetun die neue IP kennt.
        LogNewPublicIpDetached();
    }

    /// <summary>
    /// Ermittelt + loggt die neue Public-IP nach einer Rotation OHNE den Rate-Limiter zu halten
    /// (fire-and-forget, best-effort — rein zur Korrelation in ES/Kibana).
    /// </summary>
    private void LogNewPublicIpDetached()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var newIp = await TryGetPublicIpAsync(CancellationToken.None);
                using var _ = LogContext.PushProperty("LogTags", "crawl");
                if (newIp is not null)
                    _logger.LogInformation("VPN IP rotated → {NewIp}", newIp);
                else
                    _logger.LogInformation("VPN IP rotated (neue IP nicht ermittelbar)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "detached publicip logging failed");
            }
        });
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
        // POST-Antwort-Redirects werden ebenfalls manuell + je Hop geprüft gefolgt (schließt die
        // non-blind-SSRF-Lücke, bei der eine 3xx-POST-Antwort blind auf einen fremden Host führte).
        var response = await SendFollowingRedirectsAsync(
            HttpMethod.Post, new Uri(resolvedUrl), () => new FormUrlEncodedContent(formData), ct);
        response.EnsureSuccessStatusCode();

        var resultHtml = await ReadBodyBoundedAsync(response, ct);
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
        var response = await SendFollowingRedirectsAsync(
            HttpMethod.Post, new Uri(resolvedUrl), () => new FormUrlEncodedContent(formData), ct);
        response.EnsureSuccessStatusCode();

        var resultHtml = await ReadBodyBoundedAsync(response, ct);
        var results = await _parser.ParsePlayerTournamentsAsync(resultHtml);

        // Deduplicate and limit
        return results
            .GroupBy(r => r.TournamentId)
            .Select(g => g.First())
            .Take(50)
            .ToList();
    }

    /// <summary>
    /// Liest den Wert eines versteckten ASP.NET-Form-Feldes (z.B. __VIEWSTATE) per AngleSharp.
    /// Sucht zuerst über das name-Attribut, fällt auf das id-Attribut zurück. Der zurückgegebene
    /// Wert ist bereits HTML-entschlüsselt (AngleSharp dekodiert Attributwerte).
    /// Robuster gegen Markup-Drift (Attribut-Reihenfolge, Quoting, Self-Closing) als das frühere
    /// Regex. Der Parser wird je Aufruf neu erzeugt (ParseDocument ist nicht thread-safe, Crawls
    /// laufen parallel) — die Form ist klein, der Overhead vernachlässigbar.
    /// </summary>
    internal static string? ExtractHiddenField(string html, string fieldName)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var input = document.QuerySelector($"input[name=\"{fieldName}\"]")
                    ?? document.QuerySelector($"input[id=\"{fieldName}\"]");
        // Wie zuvor: ohne passendes Feld ODER ohne value-Attribut → null (das alte Regex
        // verlangte ein value-Attribut, sonst kein Treffer).
        return input?.GetAttribute("value");
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
            // Rotate VPN IP every N requests. Der Semaphor wird NUR während des eigentlichen
            // Tunnel-Neustarts (stop→pause→start) gehalten — in dieser kurzen Phase ist der Tunnel
            // unten, also DARF ohnehin kein Crawl-Request raus. Die anschließende, rein informative
            // Public-IP-Ermittlung (bis zu 5×1 s Polling, nur fürs Logging) lief früher ebenfalls
            // im Lock und blockierte alle wartenden Crawls ~5 s zusätzlich (Timeout-Risiko) →
            // läuft jetzt detached außerhalb des Locks (siehe RestartVpnTunnelAsync).
            _requestCount++;
            if (_requestCount >= _rotateAfterRequests)
            {
                _requestCount = 0;
                await RestartVpnTunnelAsync(ct);
            }

            var elapsed = (DateTime.UtcNow - _lastRequest).TotalMilliseconds;
            if (elapsed < _minDelayMs)
            {
                await Task.Delay(_minDelayMs - (int)elapsed, ct);
            }
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
