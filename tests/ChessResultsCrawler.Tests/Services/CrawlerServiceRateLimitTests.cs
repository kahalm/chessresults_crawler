using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Genau EIN Durchlauf durch den Rate-Limiter je HTTP-Anfrage an chess-results. Frueher riefen
/// die vier zustandslosen Einzelabrufe (Vereinsnamen, Turnierinfo, Rundenplan, Spielerkarte)
/// <c>RateLimitAsync</c> selbst auf und gingen danach ueber <c>FetchPageAsync</c> bzw.
/// <c>FetchWithRedirectAsync</c> ein zweites Mal durch den Riegel: +1,5 s Wartezeit je Abruf und
/// ein doppelt zaehlender Rotationszaehler — die VPN-Rotation „alle 20 Abrufe" lief schon nach
/// 10 echten (gemessen: 18 Rotationen auf 176 Abrufe im Rundenplan-Durchgang).
///
/// <para>Beobachtet wird der Riegel ueber die Rotation: mit <c>RotateAfterRequests = 1</c>
/// rotiert JEDER Durchlauf genau einmal (stop + start = zwei PUTs an den eigenen gluetun-Stub).
/// Das ist unabhaengig vom statischen Zaehler, den parallel laufende Testklassen mitbenutzen —
/// jede Instanz rotiert ueber ihr eigenes Gate.</para>
/// </summary>
public class CrawlerServiceRateLimitTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceRateLimitTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    public static TheoryData<string, int> PublicFetches => new()
    {
        // Methode, erwartete HTTP-Anfragen (ohne Redirects: GET bzw. GET + POST)
        { nameof(CrawlerService.FetchPageAsync), 1 },
        { nameof(CrawlerService.FetchTeamNamesAsync), 1 },
        { nameof(CrawlerService.FetchRoundPlanAsync), 1 },
        { nameof(CrawlerService.FetchPlayerCardAsync), 1 },
        { nameof(CrawlerService.FetchTournamentInfoAsync), 2 },
        { nameof(CrawlerService.SearchTournamentsAsync), 2 },
        { nameof(CrawlerService.FetchCalendarAsync), 2 },
        { nameof(CrawlerService.SearchPlayersAsync), 2 },
        { nameof(CrawlerService.SearchPlayerTournamentsAsync), 2 },
        { nameof(CrawlerService.SearchGamesPgnByFideAsync), 2 },
    };

    [Theory]
    [MemberData(nameof(PublicFetches))]
    public async Task PublicFetch_PassesRateLimiterExactlyOncePerHttpRequest(string method, int expectedRequests)
    {
        var rig = new Rig(_db);

        await rig.InvokeAsync(method);

        Assert.Equal(expectedRequests, rig.CrawlRequests);
        // Ein Riegel-Durchlauf = eine Rotation = zwei PUTs (stopped, running).
        Assert.Equal(expectedRequests * 2, rig.VpnStatusPuts);
    }

    // ---------------------------------------------------------------------

    private sealed class Rig
    {
        private int _crawlRequests;
        private int _vpnStatusPuts;
        private readonly CrawlerService _service;

        public int CrawlRequests => Volatile.Read(ref _crawlRequests);
        public int VpnStatusPuts => Volatile.Read(ref _vpnStatusPuts);

        public Rig(AppDbContext db)
        {
            const string formPage =
                "<html><body><h2>Testturnier</h2><form>" +
                "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"VS\" />" +
                "<input type=\"hidden\" name=\"__VIEWSTATEGENERATOR\" value=\"VSG\" />" +
                "<input type=\"hidden\" name=\"__EVENTVALIDATION\" value=\"EV\" />" +
                "</form></body></html>";
            const string resultPage = "<html><body><h2>Testturnier</h2></body></html>";

            var crawl = new HttpClient(new StubHandler(req =>
            {
                Interlocked.Increment(ref _crawlRequests);
                var body = req.Method == HttpMethod.Post ? resultPage : formPage;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
                    RequestMessage = req,
                });
            }));

            var gluetun = new HttpClient(new StubHandler(req =>
            {
                if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith("/v1/vpn/status"))
                    Interlocked.Increment(ref _vpnStatusPuts);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"public_ip":"203.0.113.7"}"""),
                });
            }));

            var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gluetun__ApiUrl"] = "http://gluetun.test:8000",
                ["Crawler:MinDelayMs"] = "0",
                ["Crawler:RetryDelayMs"] = "0",
                ["Crawler:VpnRestartPauseMs"] = "0",
                ["Crawler:RotateAfterRequests"] = "1",   // jeder Riegel-Durchlauf rotiert
            }).Build();

            _service = new CrawlerService(crawl, factory, new HtmlParserService(), db,
                Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.From(factory, config));
        }

        public Task InvokeAsync(string method) => method switch
        {
            nameof(CrawlerService.FetchPageAsync) =>
                _service.FetchPageAsync("https://chess-results.com/tnr1.aspx", "lan=1&art=0"),
            nameof(CrawlerService.FetchTeamNamesAsync) => _service.FetchTeamNamesAsync("1"),
            nameof(CrawlerService.FetchRoundPlanAsync) => _service.FetchRoundPlanAsync("1"),
            nameof(CrawlerService.FetchPlayerCardAsync) => _service.FetchPlayerCardAsync("1", 5),
            nameof(CrawlerService.FetchTournamentInfoAsync) => _service.FetchTournamentInfoAsync("1"),
            nameof(CrawlerService.SearchTournamentsAsync) =>
                _service.SearchTournamentsAsync("AUT", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            nameof(CrawlerService.FetchCalendarAsync) => _service.FetchCalendarAsync("AUT"),
            nameof(CrawlerService.SearchPlayersAsync) => _service.SearchPlayersAsync("Muster", "Max"),
            nameof(CrawlerService.SearchPlayerTournamentsAsync) =>
                _service.SearchPlayerTournamentsAsync("Muster", "Max"),
            nameof(CrawlerService.SearchGamesPgnByFideAsync) => _service.SearchGamesPgnByFideAsync("1503014"),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => handler(request);
    }
}
