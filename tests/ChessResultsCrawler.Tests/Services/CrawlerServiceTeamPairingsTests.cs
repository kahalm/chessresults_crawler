using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert ab, dass auch der Paarungs-Crawl doppelte Teamnamen verträgt: die Name→Team-Map wird
/// über BuildTeamNameMap gebaut. Mit ToDictionary flog hier eine (nicht transiente)
/// ArgumentException, die den ganzen Full-/PairingsOnly-Job als Failed beendete.
/// </summary>
public class CrawlerServiceTeamPairingsTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceTeamPairingsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kennt keine Transaktionen; der Paarungs-Upsert klammert delete+insert aber
            // bewusst in eine → Warnung ignorieren statt den Produktionscode zu verbiegen.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _html;
        public StubHandler(string html) => _html = html;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_html),
                RequestMessage = request,
            });
    }

    [Fact]
    public async Task CrawlTeamPairingsAsync_DuplicateTeamNames_DoesNotThrow_AndUsesLowestSnr()
    {
        var tournament = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        // Zwei gleichnamige Teams (Altbestand/Dublette in den Quelldaten) + ein eindeutiges.
        _db.Teams.AddRange(
            new Team { TournamentId = tournament.Id, Snr = 3, Name = "Team A" },
            new Team { TournamentId = tournament.Id, Snr = 1, Name = "Team A" },
            new Team { TournamentId = tournament.Id, Snr = 2, Name = "Team B" });
        await _db.SaveChangesAsync();

        var html = @"<html><body><table class='CRs1'>
            <tr><th>Nr.</th><th>Home</th><th>Away</th><th>Erg.</th></tr>
            <tr><td>1</td><td>Team A</td><td>Team B</td><td>3:1</td></tr>
            </table></body></html>";

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun:ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
        }).Build();
        var svc = new CrawlerService(new HttpClient(new StubHandler(html)), new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.Unused());

        await svc.CrawlTeamPairingsAsync(tournament, "https://chess-results.com/tnr1.aspx?lan=0",
            new List<int> { 1 }, CancellationToken.None);

        var pairing = Assert.Single(_db.TeamPairings.ToList());
        var homeTeam = _db.Teams.First(t => t.Id == pairing.HomeTeamId);
        Assert.Equal("Team A", homeTeam.Name);
        Assert.Equal(1, homeTeam.Snr);   // deterministisch die kleinste Snr
        Assert.Equal(3m, pairing.HomeScore);
    }

    /// <summary>Beantwortet jede Anfrage mit der Seite, die <paramref name="route"/> zur URL waehlt.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _route;
        public RoutingHandler(Func<string, string> route) => _route = route;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_route(request.RequestUri!.ToString())),
                RequestMessage = request,
            });
    }

    private CrawlerService CreateService(Func<string, string> route, ILogger<CrawlerService>? logger = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun:ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            // Der Olympiade-Crawl macht 14 Abrufe — ohne das rotierte er ueber TestVpnGate.Unused.
            ["Crawler:RotateAfterRequests"] = "1000000",
        }).Build();
        return new CrawlerService(new HttpClient(new RoutingHandler(route)), new HtmlParserService(),
            _db, logger ?? Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.Unused());
    }

    private async Task<Tournament> SeedTournamentAsync(string chessResultsId, params string[] teamNames)
    {
        var tournament = new Tournament { ChessResultsId = chessResultsId, Name = "T" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();
        _db.Teams.AddRange(teamNames.Select((name, i) => new Team { TournamentId = tournament.Id, Snr = i + 1, Name = name }));
        await _db.SaveChangesAsync();
        return tournament;
    }

    private async Task<CrawlJob> RunPairingsOnlyAsync(CrawlerService svc, string chessResultsId)
    {
        var job = new CrawlJob { ChessResultsId = chessResultsId, JobType = CrawlJobType.PairingsOnly, Status = CrawlJobStatus.Queued };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();
        return await svc.ExecuteCrawlAsync(job);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_OlympiadPairingPage_StoresTeamPairingsAndPlannedRounds()
    {
        // Regression Olympiade 2026 (tnr1469895): 11 Runden, 208 Teams, aber 0 TeamPairings und
        // TotalRounds 0. Das Fixture ist die echte (gekuerzte) Paarungsseite; sie traegt auch die
        // Turnierdetails, dient also zugleich als art=0-Seite.
        var olympiad = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "team-pairings-olympiad-tnr1469895.html"));
        var tournament = await SeedTournamentAsync("1469895",
            "Jamaica", "Uzbekistan", "Iraq", "United States of America", "India", "Thailand",
            "Netherlands", "Hong Kong, China", "Angola", "Cote d’Ivoire");

        var job = await RunPairingsOnlyAsync(CreateService(_ => olympiad), "1469895");

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.Equal(11, _db.Tournaments.Single(t => t.Id == tournament.Id).TotalRounds);

        var round1 = _db.Rounds.Single(r => r.TournamentId == tournament.Id && r.RoundNumber == 1);
        Assert.True(round1.ResultsPublished);
        var pairings = _db.TeamPairings.Where(tp => tp.RoundId == round1.Id).OrderBy(tp => tp.MatchNumber).ToList();
        // 6 Zeilen, davon 2 „nicht ausgelost" (kein echtes Team) → 4 Paarungen.
        Assert.Equal([1, 2, 3, 6], pairings.Select(p => p.MatchNumber));

        var teams = _db.Teams.Where(t => t.TournamentId == tournament.Id).ToDictionary(t => t.Id, t => t.Name);
        Assert.Equal("Jamaica", teams[pairings[0].HomeTeamId]);
        Assert.Equal("Uzbekistan", teams[pairings[0].AwayTeamId]);   // „Uzbekistan *)" ohne Fussnote
        Assert.Equal(0m, pairings[0].HomeScore);
        Assert.Equal(4m, pairings[0].AwayScore);
        Assert.Equal("Hong Kong, China", teams[pairings[3].AwayTeamId]);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_PlannedRoundsButNoDrawYet_CreatesNoRounds()
    {
        // Kommendes Turnier (so tnr1483729): die Details nennen schon „Rundenanzahl 5", art=2 hat
        // weder Tabelle noch Rundenlinks. Es darf KEINE (leere, als veroeffentlicht markierte) Runde
        // entstehen — sonst meldet die Rundenerkennung spaeter nie eine neue.
        var tournament = await SeedTournamentAsync("1483729");
        const string details = "<html><body><h2>Open 2027</h2><table>" +
                               "<tr><td class='CR'>Rundenanzahl</td><td class='CR'>5</td></tr></table></body></html>";
        const string noDraw = "<html><body><h2>Paarungen/Ergebnisse</h2></body></html>";

        var job = await RunPairingsOnlyAsync(CreateService(url => url.Contains("art=2") ? noDraw : details), "1483729");

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.Equal(5, _db.Tournaments.Single(t => t.Id == tournament.Id).TotalRounds);
        Assert.Empty(_db.Rounds.Where(r => r.TournamentId == tournament.Id));
    }

    [Fact]
    public async Task ExecuteCrawlAsync_LeagueAllRoundsPageWithoutRoundLinks_CrawlsEachPlannedRound()
    {
        // Mannschafts-Rundenturnier (Liga, so tnr1404438): art=2 zeigt „Teamauslosung aller Runden"
        // ohne einen einzigen rd=-Link; die Einzelrunde gibt es trotzdem unter art=2&rd=N.
        var tournament = await SeedTournamentAsync("1404438", "Team A", "Team B", "Team C", "Team D");
        const string details = "<html><body><h2>Liga</h2><table>" +
                               "<tr><td class='CR'>Rundenanzahl</td><td class='CR'>2</td></tr></table></body></html>";
        static string Round(string head, string rows) =>
            "<html><body><h2>" + head + "</h2><table class='CRs1'>" +
            "<tr><th>Nr.</th><th>Team</th><th>Team</th><th>Erg.</th><th>:</th><th>Erg.</th><th>Datum</th></tr>" +
            rows + "</table></body></html>";
        var allRounds = Round("Teamauslosung aller Runden",
            "<tr><td>1</td><td>Team A</td><td>Team B</td><td></td><td>:</td><td></td><td>25.11.2026</td></tr>");
        var round1 = Round("Teamauslosung",
            "<tr><td>1</td><td>Team A</td><td>Team B</td><td></td><td>:</td><td></td><td>25.11.2026</td></tr>");
        var round2 = Round("Teamauslosung",
            "<tr><td>1</td><td>Team C</td><td>Team A</td><td></td><td>:</td><td></td><td>26.11.2026</td></tr>");

        var job = await RunPairingsOnlyAsync(CreateService(url =>
            url.Contains("rd=1") ? round1 :
            url.Contains("rd=2") ? round2 :
            url.Contains("art=2") ? allRounds : details), "1404438");

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        var rounds = _db.Rounds.Where(r => r.TournamentId == tournament.Id).OrderBy(r => r.RoundNumber).ToList();
        Assert.Equal([1, 2], rounds.Select(r => r.RoundNumber));
        var teams = _db.Teams.Where(t => t.TournamentId == tournament.Id).ToDictionary(t => t.Id, t => t.Name);
        var second = Assert.Single(_db.TeamPairings.Where(tp => tp.RoundId == rounds[1].Id));
        Assert.Equal("Team C", teams[second.HomeTeamId]);
        Assert.Null(second.HomeScore);
    }
    private static int WarningCount(Mock<ILogger<CrawlerService>> logger) =>
        logger.Invocations.Count(i => i.Method.Name == nameof(ILogger.Log)
                                      && (LogLevel)i.Arguments[0] == LogLevel.Warning);

    private async Task<Round> SeedRoundAsync(Tournament tournament, int roundNumber)
    {
        var round = new Round { TournamentId = tournament.Id, RoundNumber = roundNumber, PairingsPublished = true, ResultsPublished = true };
        _db.Rounds.Add(round);
        await _db.SaveChangesAsync();
        return round;
    }

    [Theory]
    [InlineData("<html><body><h2>Wartung</h2><p>Bitte spaeter erneut versuchen.</p></body></html>")]
    // Tabelle erkannt, aber alle Teamnamen unbekannt.
    [InlineData("<html><body><table class='CRs1'><tr><th>Nr.</th><th>Home</th><th>Away</th><th>Erg.</th></tr>" +
                "<tr><td>1</td><td>Fremd X</td><td>Fremd Y</td><td>2:2</td></tr></table></body></html>")]
    public async Task CrawlTeamPairingsAsync_NoUsablePairings_KeepsStoredRound(string html)
    {
        // S2-001: Ein Re-Crawl, dessen Seite keine einzige zuordenbare Paarung liefert, leerte die
        // Runde und setzte ResultsPublished zurueck. Jetzt bleibt der Stand stehen, mit Warnung.
        var tournament = await SeedTournamentAsync("2001", "Team A", "Team B", "Team C", "Team D");
        var round = await SeedRoundAsync(tournament, 1);
        var ids = _db.Teams.Where(t => t.TournamentId == tournament.Id).OrderBy(t => t.Snr).Select(t => t.Id).ToList();
        _db.TeamPairings.AddRange(
            new TeamPairing { RoundId = round.Id, MatchNumber = 1, HomeTeamId = ids[0], AwayTeamId = ids[1], HomeScore = 3, AwayScore = 1 },
            new TeamPairing { RoundId = round.Id, MatchNumber = 2, HomeTeamId = ids[2], AwayTeamId = ids[3], HomeScore = 2, AwayScore = 2 });
        await _db.SaveChangesAsync();

        var logger = new Mock<ILogger<CrawlerService>>();
        // playersFresh: kein Spieler-Nachcrawl fuer die unbekannten Namen.
        await CreateService(_ => html, logger.Object).CrawlTeamPairingsAsync(tournament,
            "https://chess-results.com/tnr2001.aspx?lan=0", new List<int> { 1 }, CancellationToken.None, playersFresh: true);

        Assert.Equal(2, _db.TeamPairings.Count(tp => tp.RoundId == round.Id));
        Assert.True(_db.Rounds.Single(r => r.Id == round.Id).ResultsPublished);
        Assert.Equal(1, WarningCount(logger));
    }

    [Fact]
    public async Task ExecuteCrawlAsync_IndividualRoundPageWithoutTable_KeepsStoredPairings()
    {
        // S2-001, Einzelturnier: art=2 nennt Runde 1, die Rundenseite liefert aber keine Tabelle
        // (Layout/Sperrseite) — die vier gespeicherten Paarungen bleiben.
        var tournament = await SeedTournamentAsync("2002");
        var round = await SeedRoundAsync(tournament, 1);
        _db.Pairings.AddRange(Enumerable.Range(1, 4).Select(b => new Pairing { RoundId = round.Id, BoardNumber = b, Result = "1-0" }));
        await _db.SaveChangesAsync();
        const string details = "<html><body><h2>Open</h2><table>" +
                               "<tr><td class='CR'>Rundenanzahl</td><td class='CR'>1</td></tr></table></body></html>";
        const string art2 = "<html><body><h2>Paarungen</h2><a href='tnr2002.aspx?lan=0&art=2&rd=1'>Rd.1</a></body></html>";
        const string empty = "<html><body><p>Keine Daten</p></body></html>";

        var logger = new Mock<ILogger<CrawlerService>>();
        var job = await RunPairingsOnlyAsync(CreateService(url =>
            url.Contains("rd=1") ? empty : url.Contains("art=2") ? art2 : details, logger.Object), "2002");

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.Equal(4, _db.Pairings.Count(p => p.RoundId == round.Id));
        Assert.True(_db.Rounds.Single(r => r.Id == round.Id).ResultsPublished);
        Assert.True(WarningCount(logger) >= 1);
    }
}
