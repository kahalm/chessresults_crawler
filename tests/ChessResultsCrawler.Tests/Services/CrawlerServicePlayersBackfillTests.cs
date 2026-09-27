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
/// Ein PairingsOnly-Auftrag (Rundenmonitor) holt die Spieler nach, wenn eine Runde Startnummern
/// bzw. Teams nennt, die der Bestand nicht kennt.
///
/// <para>Anlass: die Schachrallye Pradl 2026 (tnr1503220) wurde am Morgen gemerkt, als auf
/// chess-results noch keine Startliste stand — der Full-Crawl holte richtig 0 Spieler. Danach
/// kamen nur noch PairingsOnly-Auftraege, und die holten die Spieler nie: die Spielerliste blieb
/// leer und jede Paarung namenlos.</para>
/// </summary>
public class CrawlerServicePlayersBackfillTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly List<string> _requests = [];

    public CrawlerServicePlayersBackfillTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private sealed class RoutingHandler(Func<string, string> route, List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route(url)),
                RequestMessage = request,
            });
        }
    }

    private CrawlerService CreateService(Func<string, string> route)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:RotateAfterRequests"] = "1000000",
        }).Build();
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        return new CrawlerService(new HttpClient(new RoutingHandler(route, _requests)), factory, new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.Unused());
    }

    private async Task<CrawlJob> RunAsync(CrawlerService svc, string chessResultsId, CrawlJobType type)
    {
        var job = new CrawlJob { ChessResultsId = chessResultsId, JobType = type, Status = CrawlJobStatus.Queued };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();
        return await svc.ExecuteCrawlAsync(job);
    }

    private const string Details = "<html><body><h2>Schachrallye</h2></body></html>";
    private const string NoPlayers = "<html><body><h2>Spieler nach Elo sortiert</h2><table class='CRs1'>" +
                                     "<tr><th>Nr.</th><th>Name</th></tr></table></body></html>";
    private const string Players = "<html><body><table>" +
        "<tr><th>Nr.</th><th>Title</th><th>Name</th><th>FideID</th><th>Rtg</th><th>FED</th><th>Team</th><th>Br.</th></tr>" +
        "<tr><td>1</td><td></td><td>Alpha, Anna</td><td>1</td><td>1900</td><td>AUT</td><td></td><td></td></tr>" +
        "<tr><td>2</td><td></td><td>Beta, Bert</td><td>2</td><td>1800</td><td>AUT</td><td></td><td></td></tr>" +
        "<tr><td>3</td><td></td><td>Gamma, Gustav</td><td>3</td><td>1700</td><td>AUT</td><td></td><td></td></tr>" +
        "<tr><td>4</td><td></td><td>Delta, Dora</td><td>4</td><td>1600</td><td>AUT</td><td></td><td></td></tr>" +
        "</table></body></html>";
    private const string Round1 = "<html><body><h2>Paarungen</h2><a href='tnr1503220.aspx?art=2&rd=1'>Rd.1</a>" +
        "<table class='CRs1'>" +
        "<tr><th>Br.</th><th>Nr</th><th>Ti.</th><th>Name</th><th>Elo</th><th>Pts</th><th>Result</th><th>Pts</th><th>Ti.</th><th>Name</th><th>Elo</th><th>Nr</th></tr>" +
        "<tr><td>1</td><td>1</td><td></td><td>Alpha, Anna</td><td>1900</td><td>0</td><td>1-0</td><td>0</td><td></td><td>Gamma, Gustav</td><td>1700</td><td>3</td></tr>" +
        "<tr><td>2</td><td>4</td><td></td><td>Delta, Dora</td><td>1600</td><td>0</td><td>0-1</td><td>0</td><td></td><td>Beta, Bert</td><td>1800</td><td>2</td></tr>" +
        "</table></body></html>";

    [Fact]
    public async Task PairingsOnly_WithUnknownStartNumbers_CrawlsThePlayersOnce()
    {
        var tournament = new Tournament { ChessResultsId = "1503220", Name = "Schachrallye Pradl 2026" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        var job = await RunAsync(CreateService(url =>
            url.Contains("art=16") ? NoPlayers :
            url.Contains("zeilen") ? Players :
            url.Contains("art=2") ? Round1 : Details), "1503220", CrawlJobType.PairingsOnly);

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.Equal(4, _db.Players.Count(p => p.TournamentId == tournament.Id));

        var names = _db.Players.Where(p => p.TournamentId == tournament.Id).ToDictionary(p => p.Id, p => p.Name);
        var pairings = _db.Pairings.OrderBy(p => p.BoardNumber).ToList();
        Assert.Equal(2, pairings.Count);
        Assert.Equal("Alpha, Anna", names[pairings[0].WhitePlayerId!.Value]);
        Assert.Equal("Beta, Bert", names[pairings[1].BlackPlayerId!.Value]);

        // Einmal je Auftrag — nicht je Runde, und art=16 vor art=0 wie im Full-Crawl.
        Assert.Equal(1, _requests.Count(u => u.Contains("zeilen") && u.Contains("art=0")));
    }

    /// <summary>Kennt der Bestand alle Startnummern, bleibt ein PairingsOnly-Auftrag so leicht wie bisher.</summary>
    [Fact]
    public async Task PairingsOnly_WithKnownPlayers_DoesNotCrawlThePlayers()
    {
        var tournament = new Tournament { ChessResultsId = "1503220", Name = "Schachrallye Pradl 2026" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();
        _db.Players.AddRange(Enumerable.Range(1, 4).Select(i => new Player { TournamentId = tournament.Id, Snr = i, Name = $"P{i}" }));
        await _db.SaveChangesAsync();

        var job = await RunAsync(CreateService(url =>
            url.Contains("art=16") ? NoPlayers :
            url.Contains("zeilen") ? Players :
            url.Contains("art=2") ? Round1 : Details), "1503220", CrawlJobType.PairingsOnly);

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.DoesNotContain(_requests, u => u.Contains("zeilen") || u.Contains("art=16"));
    }

    /// <summary>
    /// Im Full-Crawl kamen die Spieler eben erst: eine Startnummer, die auch dort fehlt (Datenfehler
    /// auf chess-results), loest keinen zweiten Spieler-Abruf aus.
    /// </summary>
    [Fact]
    public async Task Full_DoesNotCrawlThePlayersTwice()
    {
        var tournament = new Tournament { ChessResultsId = "1503220", Name = "Schachrallye Pradl 2026" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        await RunAsync(CreateService(url =>
            url.Contains("art=16") ? NoPlayers :
            url.Contains("zeilen") ? NoPlayers :
            url.Contains("art=2") ? Round1 : Details), "1503220", CrawlJobType.Full);

        Assert.Equal(1, _requests.Count(u => u.Contains("zeilen") && u.Contains("art=0")));
    }

    /// <summary>
    /// Dasselbe fuer Mannschaftsturniere: die Teams entstehen im Spieler-Crawl. Kannte der Bestand
    /// keins, war jede Paarung „Team not found".
    /// </summary>
    [Fact]
    public async Task PairingsOnly_WithUnknownTeams_CrawlsThePlayersAndStoresTheMatch()
    {
        var tournament = new Tournament { ChessResultsId = "1404438", Name = "Liga" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        const string roster = "<html><body><table>" +
            "<tr><th>Nr.</th><th>Title</th><th>Name</th><th>FideID</th><th>Rtg</th><th>FED</th><th>Team</th><th>Br.</th></tr>" +
            "<tr><td>1</td><td></td><td>Alpha, Anna</td><td>1</td><td>1900</td><td>AUT</td><td>Team A</td><td>1</td></tr>" +
            "<tr><td>2</td><td></td><td>Beta, Bert</td><td>2</td><td>1800</td><td>AUT</td><td>Team B</td><td>1</td></tr>" +
            "</table></body></html>";
        const string round = "<html><body><h2>Teamauslosung</h2><a href='tnr1404438.aspx?art=2&rd=1'>Rd.1</a>" +
            "<table class='CRs1'>" +
            "<tr><th>Nr.</th><th>Team</th><th>Team</th><th>Erg.</th><th>:</th><th>Erg.</th><th>Datum</th></tr>" +
            "<tr><td>1</td><td>Team A</td><td>Team B</td><td>2½</td><td>:</td><td>1½</td><td>25.11.2026</td></tr>" +
            "</table></body></html>";

        var job = await RunAsync(CreateService(url =>
            url.Contains("art=16") ? roster :
            url.Contains("art=2") ? round : Details), "1404438", CrawlJobType.PairingsOnly);

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        var teams = _db.Teams.Where(t => t.TournamentId == tournament.Id).ToDictionary(t => t.Id, t => t.Name);
        var match = Assert.Single(_db.TeamPairings);
        Assert.Equal("Team A", teams[match.HomeTeamId]);
        Assert.Equal("Team B", teams[match.AwayTeamId]);
    }
}
