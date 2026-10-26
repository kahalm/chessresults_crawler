using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ChessResultsCrawler.Services.TeamNameMatching;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Der Paarungs-Teil eines Crawl-Auftrags (art=2): verfuegbare Runden erkennen, Mannschafts- bzw.
/// Einzelpaarungen je Runde in einer Transaktion ersetzen, unbekannte Teams/Startnummern einmal je
/// Auftrag per Spieler-Crawl nachholen. Aus <see cref="TournamentCrawlJob"/> herausgezogen
/// (Zerlegung, zweiter Schnitt); der Spieler-Crawl bleibt dort und kommt als Delegat herein.
/// Logger-Kategorie bleibt <see cref="CrawlerService"/> (log-watcher).
/// </summary>
internal sealed class PairingsCrawl
{
    private readonly ChessResultsTransport _transport;
    private readonly HtmlParserService _parser;
    private readonly AppDbContext _db;
    private readonly ILogger<CrawlerService> _logger;
    private readonly Func<Tournament, string, CancellationToken, Task> _crawlPlayers;

    public PairingsCrawl(ChessResultsTransport transport, HtmlParserService parser, AppDbContext db,
        ILogger<CrawlerService> logger, Func<Tournament, string, CancellationToken, Task> crawlPlayers)
    {
        _transport = transport;
        _parser = parser;
        _db = db;
        _logger = logger;
        _crawlPlayers = crawlPlayers;
    }

    /// <param name="playersFresh">Die Spielerliste wurde in DIESEM Auftrag eben geholt (Full) — dann
    /// holt der Paarungs-Crawl sie bei unbekannten Startnummern nicht ein zweites Mal.</param>
    internal async Task CrawlAllPairingsAsync(Tournament tournament, string baseUrl, CancellationToken ct,
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
                await _crawlPlayers(tournament, baseUrl, ct);
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
                await _crawlPlayers(tournament, baseUrl, ct);
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
