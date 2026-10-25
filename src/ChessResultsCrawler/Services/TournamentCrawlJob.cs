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
/// ueber <see cref="ChessResultsTransport"/>; gebaut von <see cref="CrawlerService"/>.
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
                await CrawlAllPairingsAsync(tournament, resolvedUrl, ct, playersFresh: true);
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

    /// <param name="playersFresh">Die Spielerliste wurde in DIESEM Auftrag eben geholt (Full) — dann
    /// holt der Paarungs-Crawl sie bei unbekannten Startnummern nicht ein zweites Mal.</param>
    private async Task CrawlAllPairingsAsync(Tournament tournament, string baseUrl, CancellationToken ct,
        bool playersFresh = false)
    {
        _logger.LogInformation("Crawling pairings for tournament {Id}", tournament.ChessResultsId);

        // First discover available rounds from art=2
        var art2Html = await _transport.FetchPageAsync(baseUrl, "art=2", ct);
        var availableRounds = await _parser.ParseAvailableRoundsAsync(art2Html, tournament.TotalRounds);

        // Ohne Rundenlinks alle Runden 1..TotalRounds annehmen — so sieht es bei Mannschafts-
        // Rundenturnieren (Ligen) aus: „Teamauslosung aller Runden" auf EINER Seite, kein einziger
        // rd=-Link, art=2&rd=N liefert trotzdem die einzelne Runde. Aber nur, wenn die Seite schon
        // eine Paarungstabelle traegt: TotalRounds ist die GEPLANTE Rundenzahl aus den
        // Turnierdetails („Rundenanzahl"), die vor der ersten Auslosung schon dasteht. Ohne diese
        // Bedingung legte der Crawl eines kommenden Turniers N leere Runden an — und die
        // Rundenerkennung meldete danach keine Runde je wieder als neu.
        if (availableRounds.Count == 0 && tournament.TotalRounds > 0
            && await _parser.HasPairingsTableAsync(art2Html))
        {
            availableRounds = Enumerable.Range(1, tournament.TotalRounds).ToList();
        }

        // Detect tournament type from first round page
        var isTeam = await _parser.IsTeamPairingsPageAsync(art2Html);
        _logger.LogInformation("Tournament {Id} pairings type: {Type}", tournament.ChessResultsId, isTeam ? "Team" : "Individual");

        if (isTeam)
        {
            await CrawlTeamPairingsAsync(tournament, baseUrl, availableRounds, ct, playersFresh);
        }
        else
        {
            await CrawlIndividualPairingsAsync(tournament, baseUrl, availableRounds, ct, playersFresh);
        }
    }

    // internal (statt private) nur für den Regressionstest mit doppelten Teamnamen.
    internal async Task CrawlTeamPairingsAsync(Tournament tournament, string baseUrl, List<int> availableRounds,
        CancellationToken ct, bool playersFresh = false)
    {
        // Wie im Spieler-Crawl tolerant gegen doppelte Teamnamen (Altbestand aus der Zeit vor
        // BuildTeamNameMap oder echte Dubletten in den Quelldaten): ToDictionary würde hier eine
        // nicht-transiente ArgumentException werfen und den Full-/PairingsOnly-Job hart als Failed
        // beenden — das Turnier wäre danach gar nicht mehr crawlbar.
        var teams = BuildTeamNameMap(
            await _db.Teams.Where(t => t.TournamentId == tournament.Id).ToListAsync(ct));

        foreach (var roundNum in availableRounds)
        {
            ct.ThrowIfCancellationRequested();
            var round = await GetOrCreateRoundAsync(tournament.Id, roundNum, ct);

            var roundHtml = await _transport.FetchPageAsync(baseUrl, $"art=2&rd={roundNum}", ct);
            var parsedPairings = await _parser.ParseTeamPairingsAsync(roundHtml);
            _logger.LogInformation("Round {Round}: parsed {Count} team pairings", roundNum, parsedPairings.Count);

            // Teams, die der Bestand nicht kennt: die Teams entstehen im SPIELER-Crawl, und ein
            // PairingsOnly-Auftrag holte den nie. Stand beim ersten Abruf noch keine Aufstellung auf
            // chess-results, blieb jede Paarung danach „Team not found". Einmal je Auftrag nachholen.
            if (!playersFresh && parsedPairings.Any(pp =>
                    (FindTeam(teams, pp.HomeTeamName) is null && !IsByeOpponent(pp.HomeTeamName))
                    || (FindTeam(teams, pp.AwayTeamName) is null && !IsByeOpponent(pp.AwayTeamName))))
            {
                _logger.LogInformation("Round {Round}: unknown teams, re-crawling players of {Id}", roundNum, tournament.ChessResultsId);
                await CrawlPlayersAsync(tournament, baseUrl, ct);
                teams = BuildTeamNameMap(
                    await _db.Teams.Where(t => t.TournamentId == tournament.Id).ToListAsync(ct));
                playersFresh = true;
            }

            // H-9: Wrap delete+insert in transaction for re-crawl safety
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var existingPairings = await _db.TeamPairings
                .Where(tp => tp.RoundId == round.Id)
                .ToListAsync(ct);
            _db.TeamPairings.RemoveRange(existingPairings);

            foreach (var pp in parsedPairings)
            {
                var homeTeam = FindTeam(teams, pp.HomeTeamName);
                var awayTeam = FindTeam(teams, pp.AwayTeamName);

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

    private async Task CrawlIndividualPairingsAsync(Tournament tournament, string baseUrl, List<int> availableRounds,
        CancellationToken ct, bool playersFresh = false)
    {
        var playersBySnr = await _db.Players
            .Where(p => p.TournamentId == tournament.Id)
            .ToDictionaryAsync(p => p.Snr, ct);

        foreach (var roundNum in availableRounds)
        {
            ct.ThrowIfCancellationRequested();
            var round = await GetOrCreateRoundAsync(tournament.Id, roundNum, ct);

            var roundHtml = await _transport.FetchPageAsync(baseUrl, $"art=2&rd={roundNum}", ct);
            var parsedPairings = await _parser.ParseIndividualPairingsAsync(roundHtml);
            _logger.LogInformation("Round {Round}: parsed {Count} individual pairings", roundNum, parsedPairings.Count);

            // Startnummern, die die Spielerliste nicht kennt: ein PairingsOnly-Auftrag (Rundenmonitor)
            // holte die Spieler nie. Stand beim ersten Abruf noch keine Startliste auf chess-results —
            // bei Schnellschach-Rallyes tragen die Schiedsrichter sie oft erst am Morgen ein —, blieb
            // die Spielerliste leer und jede Paarung namenlos. Nachmeldungen genauso. Einmal je Auftrag.
            if (!playersFresh && parsedPairings.Any(pp =>
                    (pp.WhiteSnr > 0 && !playersBySnr.ContainsKey(pp.WhiteSnr))
                    || (pp.BlackSnr > 0 && !playersBySnr.ContainsKey(pp.BlackSnr))))
            {
                _logger.LogInformation("Round {Round}: unknown players, re-crawling players of {Id}", roundNum, tournament.ChessResultsId);
                await CrawlPlayersAsync(tournament, baseUrl, ct);
                playersBySnr = await _db.Players
                    .Where(p => p.TournamentId == tournament.Id)
                    .ToDictionaryAsync(p => p.Snr, ct);
                playersFresh = true;
            }

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
}
