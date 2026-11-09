using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Mannschaftsturnier OHNE Rundenzahl in den Turnierdetails: tnr1221888 („2. Bundesliga West AUT
/// 2025-26") importierte 208 Spieler und 11 Mannschaften, aber NULL Runden. Der GET auf
/// <c>art=0&amp;turdet=YES</c> klappt die Details nicht aus (das tut nur der Postback), die Seite zeigt
/// eine Kreuztabelle ohne „nach N Runden"; art=2 ist die „Teamauslosung aller Runden" ohne einen
/// einzigen <c>rd=</c>-Link. <c>TotalRounds</c> blieb 0, der Fallback <c>1..TotalRounds</c> griff nicht —
/// leer, ohne Fehler, ohne Hinweis.
///
/// <para>Die drei Fixtures sind die ECHTEN Seiten (abgerufen 2026-11-09, lan=0): art=0, art=2 und
/// art=2&amp;rd=1.</para>
/// </summary>
public class LeagueWithoutRoundCountTests : IDisposable
{
    private readonly AppDbContext _db;

    public LeagueWithoutRoundCountTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
    }

    public void Dispose() => _db.Dispose();

    private readonly HtmlParserService _parser = new();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static readonly string[] Teams =
    [
        "SPG Kufstein/Wörgl", "Schachclub Schwaz", "SK Absam", "SK Sparkasse Jenbach", "Götzis 1",
        "SK Hohenems", "SK Dornbirn 1", "Sonnenberg", "ASK Salzburg", "Ranshofen", "SK Schwarzach",
    ];

    [Fact]
    public async Task Art0Page_CarriesNoRoundCount_ThatIsTheCause()
    {
        Assert.Null(await _parser.ParseTotalRoundsAsync(Fixture("league-1221888-art0.html")));
        Assert.Empty(await _parser.ParseAvailableRoundsAsync(Fixture("league-1221888-art2.html")));
    }

    [Fact]
    public async Task ParseRoundHeadingsAsync_AllRoundsPage_ReturnsElevenRounds()
    {
        Assert.Equal(Enumerable.Range(1, 11), await _parser.ParseRoundHeadingsAsync(Fixture("league-1221888-art2.html")));
        Assert.Equal([1], await _parser.ParseRoundHeadingsAsync(Fixture("league-1221888-art2-rd1.html")));
        // Die Kreuztabelle traegt keine Rundenueberschriften (ihre Spalten 1..11 sind Teams).
        Assert.Empty(await _parser.ParseRoundHeadingsAsync(Fixture("league-1221888-art0.html")));
    }

    [Theory]
    [InlineData("<td colspan='9'>Round 4</td>", new[] { 4 })]
    [InlineData("<td>12. Runde</td><td>3. Runde</td>", new[] { 3, 12 })]
    // Fliesstext und Ueberschriften AUSSERHALB der Paarungstabelle zaehlen nicht.
    [InlineData("<td>Stand nach 7 Runden</td>", new int[0])]
    public async Task ParseRoundHeadingsAsync_OnlyWholeCellHeadingsInsideThePairingsTable(string cells, int[] expected)
    {
        var html = "<html><body><h2>3. Runde</h2><table class='CRs1'><tr>" + cells + "</tr></table></body></html>";
        Assert.Equal(expected, await _parser.ParseRoundHeadingsAsync(html));
    }

    private sealed class RoutingHandler(Func<string, string> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route(request.RequestUri!.ToString())),
                RequestMessage = request,
            });
    }

    private CrawlerService CreateService(Func<string, string> route, ILogger<CrawlerService> logger)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun:ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:RotateAfterRequests"] = "1000000",
        }).Build();
        return new CrawlerService(new HttpClient(new RoutingHandler(route)), new HtmlParserService(),
            _db, logger, config, TestVpnGate.Unused());
    }

    private async Task<(Tournament Tournament, CrawlJob Job)> RunPairingsOnlyAsync(string chessResultsId,
        Func<string, string> route, ILogger<CrawlerService> logger, params string[] teams)
    {
        var tournament = new Tournament { ChessResultsId = chessResultsId, Name = "T" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();
        _db.Teams.AddRange(teams.Select((name, i) => new Team { TournamentId = tournament.Id, Snr = i + 1, Name = name }));
        var job = new CrawlJob { ChessResultsId = chessResultsId, JobType = CrawlJobType.PairingsOnly, Status = CrawlJobStatus.Queued };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();
        job = await CreateService(route, logger).ExecuteCrawlAsync(job);
        return (_db.Tournaments.Single(t => t.Id == tournament.Id), job);
    }

    private static int WarningCount(Mock<ILogger<CrawlerService>> logger) =>
        logger.Invocations.Count(i => i.Method.Name == nameof(ILogger.Log)
                                      && (LogLevel)i.Arguments[0] == LogLevel.Warning);

    [Fact]
    public async Task ExecuteCrawlAsync_LeagueWithoutRoundCount_TakesRoundsFromHeadings()
    {
        var art0 = Fixture("league-1221888-art0.html");
        var all = Fixture("league-1221888-art2.html");
        var rd1 = Fixture("league-1221888-art2-rd1.html");
        // Runden 2..11: eine Seite ohne Tabelle — hier geht es um die Rundenzahl, nicht um elf Abrufe.
        const string empty = "<html><body><h2>Teamauslosung</h2></body></html>";
        var logger = new Mock<ILogger<CrawlerService>>();

        var (tournament, job) = await RunPairingsOnlyAsync("1221888", url =>
            url.EndsWith("&rd=1") ? rd1 :
            url.Contains("&rd=") ? empty :
            url.Contains("art=2") ? all : art0, logger.Object, Teams);

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.Equal(11, tournament.TotalRounds);
        var rounds = _db.Rounds.Where(r => r.TournamentId == tournament.Id).OrderBy(r => r.RoundNumber).ToList();
        Assert.Equal(Enumerable.Range(1, 11), rounds.Select(r => r.RoundNumber));
        // Sechs Zeilen, eine davon „spielfrei" → fuenf Paarungen.
        var first = _db.TeamPairings.Where(tp => tp.RoundId == rounds[0].Id).OrderBy(tp => tp.MatchNumber).ToList();
        Assert.Equal([1, 2, 3, 4, 5], first.Select(p => p.MatchNumber));
        var names = _db.Teams.Where(t => t.TournamentId == tournament.Id).ToDictionary(t => t.Id, t => t.Name);
        Assert.Equal("SPG Kufstein/Wörgl", names[first[0].HomeTeamId]);
        Assert.Equal(4.5m, first[0].AwayScore);
        Assert.False(TournamentResponse.FromEntity(tournament).RoundCountUnknown);
        Assert.Equal(0, WarningCount(logger));
    }

    [Fact]
    public async Task ExecuteCrawlAsync_PairingsButNoRoundAnywhere_WarnsAndFlagsTheResponse()
    {
        const string art0 = "<html><body><h2>Liga</h2><p>Kreuztabelle</p></body></html>";
        const string art2 = "<html><body><h2>Teamauslosung aller Runden</h2><table class='CRs1'>" +
                            "<tr><th>Nr.</th><th>Team</th><th>Team</th><th>Erg.</th><th>:</th><th>Erg.</th></tr>" +
                            "<tr><td>1</td><td>Team A</td><td>Team B</td><td></td><td>:</td><td></td></tr></table></body></html>";
        var logger = new Mock<ILogger<CrawlerService>>();

        var (tournament, job) = await RunPairingsOnlyAsync("9990001",
            url => url.Contains("art=2") ? art2 : art0, logger.Object, "Team A", "Team B");

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.Equal(0, tournament.TotalRounds);
        Assert.Empty(_db.Rounds.Where(r => r.TournamentId == tournament.Id));
        Assert.Equal(1, WarningCount(logger));
        Assert.True(TournamentResponse.FromEntity(tournament).RoundCountUnknown);
    }
}
