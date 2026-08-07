using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert den VPN-Rotations-Fix ab: die Rotation startet zwar den Tunnel synchron neu
/// (stop→start, unter dem Rate-Limiter-Lock, weil der Tunnel dabei unten ist), aber die rein
/// informative Public-IP-Ermittlung (bis zu 5×1 s Polling) läuft NICHT mehr inline im Lock —
/// sonst blockierte jede Rotation alle wartenden Crawls ~5 s zusätzlich (Timeout-Risiko).
/// </summary>
public class CrawlerServiceVpnRotationTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceVpnRotationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _handler(request);
    }

    [Fact]
    public async Task Rotation_RestartsTunnelUnderLock_ButDoesNotPollPublicIpInline()
    {
        int putStatusCalls = 0;
        int publicIpCalls = 0;

        // gluetun-Control-Server: PUT /v1/vpn/status (stop/start) sofort OK; GET /v1/publicip/ip
        // zählt Aufrufe. Die detachte IP-Ermittlung wartet zuerst 1 s (TryGetPublicIpAsync) →
        // sie darf bis zum Rückkehren von FetchPageAsync (im ms-Bereich) noch NICHT gefeuert haben.
        var gluetun = new HttpClient(new RecordingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put && path.EndsWith("/v1/vpn/status"))
            {
                Interlocked.Increment(ref putStatusCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            if (req.Method == HttpMethod.Get && path.EndsWith("/v1/publicip/ip"))
            {
                Interlocked.Increment(ref publicIpCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"public_ip":"203.0.113.7"}"""),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        // Crawl-Client: liefert eine gültige chess-results.com-Seite.
        var crawl = new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><h2>T</h2></body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://chess-results.com/tnr1.aspx?lan=0"),
            })));

        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://gluetun.test:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",           // keine Inter-Request-Wartezeit → Rückkehr im ms-Bereich
            ["Crawler:VpnRestartPauseMs"] = "0",    // kein 3-s-Neustart-Delay im Test
            ["Crawler:RotateAfterRequests"] = "1",  // erste Anfrage rotiert bereits
        }).Build();

        var service = new CrawlerService(crawl, factory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config);

        // Act: ein Fetch → genau eine Rotation.
        var body = await service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0");

        // Tunnel wurde synchron neu gestartet (stop + start = 2 PUTs)...
        Assert.Equal(2, putStatusCalls);
        // ...aber die Public-IP-Ermittlung lief NICHT inline im Lock (sonst hätte der GET
        // — nach seinem 1-s-Vorlauf — bis zur Rückkehr längst gefeuert bzw. blockiert).
        Assert.Equal(0, publicIpCalls);
        Assert.Contains("<h2>T</h2>", body);
    }

    [Fact]
    public async Task Rotation_CallerCancelledBetweenStopAndStart_StillStartsTunnel()
    {
        // Regression: stop→pause→start lief früher komplett mit dem Aufrufer-Token. Wurde der
        // Request genau zwischen stop und start abgebrochen (Proxy-Timeout/Deploy), blieb der
        // gluetun-Tunnel dauerhaft "stopped" und JEDER weitere Crawl scheiterte auf
        // Verbindungsebene. Die Rotation muss deshalb auch nach dem Abbruch zu Ende laufen.
        using var cts = new CancellationTokenSource();
        var putBodies = new List<string>();

        var gluetun = new HttpClient(new RecordingHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put && path.EndsWith("/v1/vpn/status"))
            {
                var body = await req.Content!.ReadAsStringAsync();
                lock (putBodies) putBodies.Add(body);
                // Abbruch direkt nach dem stop simulieren.
                if (body.Contains("stopped")) await cts.CancelAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var crawl = new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><h2>T</h2></body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://chess-results.com/tnr1.aspx?lan=0"),
            })));

        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://gluetun.test:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:VpnRestartPauseMs"] = "20",   // kurze, aber echte Pause zwischen stop und start
            ["Crawler:RotateAfterRequests"] = "1",
        }).Build();

        var service = new CrawlerService(crawl, factory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config);

        // Der Fetch selbst darf am gecancelten Token scheitern — die Rotation nicht.
        try
        {
            await service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0", cts.Token);
        }
        catch (OperationCanceledException) { /* erwartet: der Aufrufer-Request ist abgebrochen */ }

        lock (putBodies)
        {
            Assert.Equal(2, putBodies.Count);
            Assert.Contains("stopped", putBodies[0]);
            Assert.Contains("running", putBodies[1]);   // Tunnel bleibt NICHT gestoppt zurück
        }
    }

    [Fact]
    public async Task Rotation_StopRequestThrows_StillSendsRecoveryStart()
    {
        // Der gefährlichste Fall: das stop-PUT wirft (Timeout/Verbindungsabbruch beim Antwort-
        // Lesen) — gluetun kann es trotzdem schon ausgeführt haben. Ohne Recovery bliebe der
        // Tunnel dauerhaft gestoppt. Genau deshalb wird `stopSent` VOR dem Senden gesetzt.
        var putBodies = new List<string>();

        var gluetun = new HttpClient(new RecordingHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Put && path.EndsWith("/v1/vpn/status"))
            {
                var body = await req.Content!.ReadAsStringAsync();
                lock (putBodies) putBodies.Add(body);
                // Das stop scheitert NACH dem Absenden — der Tunnel ist womöglich trotzdem aus.
                if (body.Contains("stopped")) throw new HttpRequestException("connection reset");
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var crawl = new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><h2>T</h2></body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://chess-results.com/tnr1.aspx?lan=0"),
            })));

        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://gluetun.test:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:VpnRestartPauseMs"] = "20",
            ["Crawler:RotateAfterRequests"] = "1",
        }).Build();

        var service = new CrawlerService(crawl, factory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config);

        await service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0", CancellationToken.None);

        lock (putBodies)
        {
            Assert.Equal(2, putBodies.Count);
            Assert.Contains("stopped", putBodies[0]);
            Assert.Contains("running", putBodies[1]);   // Recovery-start trotz geworfenem stop
        }
    }
}
