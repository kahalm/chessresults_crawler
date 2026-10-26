using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using static ChessResultsCrawler.Services.TeamNameMatching;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Der Crawl-Ablauf EINES Auftrags: Turnier aufloesen und anlegen, Spieler- und Paarungs-Upserts
/// je Runde (in Transaktionen), Spielerdetails, Re-Queue bei Verbindungsfehlern. Holt die Seiten
/// ueber <see cref="ChessResultsTransport"/>; gebaut von <see cref="CrawlerService"/>. Der Paarungs-Teil
/// (art=2, je Runde) liegt in <see cref="PairingsCrawl"/>.
/// </summary>
internal sealed class TournamentCrawlJob
{
    private readonly ChessResultsTransport _transport;
    private readonly HtmlParserService _parser;
    private readonly AppDbContext _db;
    private readonly ILogger<CrawlerService> _logger;
    private readonly int _crawlMaxAttempts;
    private readonly int _crawlRetryBackoffSeconds;
    // Re-Queue eines kompletten Crawls bei Verbindungsfehlern (z.B. VPN-Tunnel kurz weg nach
    // Rotation/Deploy): bis zu CrawlMaxAttempts Anläufe mit gestuftem Backoff, statt sofort Failed.
    private const int DefaultCrawlMaxAttempts = 4;
    private static readonly int[] DefaultCrawlRetryBackoffSeconds = { 15, 30, 45 };

    public TournamentCrawlJob(ChessResultsTransport transport, HtmlParserService parser, AppDbContext db,
        ILogger<CrawlerService> logger, IConfiguration configuration)
    {
        _transport = transport;
        _parser = parser;
        _db = db;
        _logger = logger;
        _crawlMaxAttempts = Math.Max(1, configuration.GetValue("Crawler:CrawlMaxAttempts", DefaultCrawlMaxAttempts));
        // -1 ⇒ gestufte Default-Backoffs; >=0 ⇒ flacher Wert (v.a. für Tests, um schnell zu sein).
        _crawlRetryBackoffSeconds = configuration.GetValue("Crawler:CrawlRetryBackoffSeconds", -1);
        Pairings = new PairingsCrawl(transport, parser, db, logger, CrawlPlayersAsync);
    }

    /// <summary>Paarungs-Teil des Crawls (art=2); internal fuer den Regressionstest ueber
    /// <see cref="CrawlerService"/>.</summary>
    internal PairingsCrawl Pairings { get; }

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
                await RunCrawlOnceAsync(job, ct);
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
            catch (Exception ex) when (ChessResultsTransport.IsTransientConnectionError(ex) && attempt < _crawlMaxAttempts)
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

    /// <summary>Ein Anlauf des Crawls: Turnier aufloesen/anlegen, Kopfdaten (art=0 mit turdet=YES),
    /// dann je <see cref="CrawlJob.JobType"/> Spieler und/oder Paarungen. Fehler propagieren an die
    /// Re-Queue-Schleife in <see cref="ExecuteCrawlAsync"/>.</summary>
    private async Task RunCrawlOnceAsync(CrawlJob job, CancellationToken ct)
    {
        // Resolve tournament base URL and SNode
        var baseUrl = $"https://chess-results.com/tnr{job.ChessResultsId}.aspx?lan=0";
        var (resolvedUrl, html) = await _transport.FetchWithRedirectAsync(baseUrl, ct);

        // S-7: SSRF protection – only allow redirects to chess-results.com
        ChessResultsTransport.EnsureChessResultsHost(resolvedUrl);

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
        var art0Html = await _transport.FetchPageAsync(resolvedUrl, "art=0&turdet=YES", ct);
        var totalRounds = await _parser.ParseTotalRoundsAsync(art0Html);
        if (totalRounds.HasValue)
            tournament.TotalRounds = totalRounds.Value;

        // Parse tournament date and location from turdet details
        var details = await _parser.ParseTournamentDetailsAsync(art0Html);
        if (details.Location is not null)
            tournament.Location = details.Location;
        if (details.DateText is not null)
            tournament.DateText = details.DateText;
        tournament.GroupsJson = TournamentGroups.Serialize(details.Groups, tournament.ChessResultsId);

        switch (job.JobType)
        {
            case CrawlJobType.Full:
                await CrawlPlayersAsync(tournament, resolvedUrl, ct);
                await Pairings.CrawlAllPairingsAsync(tournament, resolvedUrl, ct, playersFresh: true);
                break;
            case CrawlJobType.PlayersOnly:
                await CrawlPlayersAsync(tournament, resolvedUrl, ct);
                break;
            case CrawlJobType.PairingsOnly:
                await Pairings.CrawlAllPairingsAsync(tournament, resolvedUrl, ct);
                break;
            case CrawlJobType.CheckNewRounds:
                // Handled by RoundDetectionService
                break;
            case CrawlJobType.PlayerDetails:
                // Handled by CrawlPlayerDetailsAsync (called directly)
                break;
        }

        await _db.SaveChangesAsync(ct);
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

                var html = await _transport.FetchPageAsync(baseUrl, $"art=9&snr={snr}", ct);
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
        var html = await _transport.FetchPageAsync(baseUrl, "art=16&zeilen=99999", ct);
        var parsedPlayers = await _parser.ParsePlayerListAsync(html);
        _logger.LogInformation("art=16: parsed {Count} players", parsedPlayers.Count);

        if (parsedPlayers.Count == 0)
        {
            html = await _transport.FetchPageAsync(baseUrl, "art=0&zeilen=99999", ct);
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
}
