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
            Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.From(factory, config));

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
            Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.From(factory, config));

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
            Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.From(factory, config));

        await service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0", CancellationToken.None);

        lock (putBodies)
        {
            Assert.Equal(2, putBodies.Count);
            Assert.Contains("stopped", putBodies[0]);
            Assert.Contains("running", putBodies[1]);   // Recovery-start trotz geworfenem stop
        }
    }

    // ----- Antwort des Steuer-Servers pruefen, laufende Anfragen abwarten (W4s S3-009) -----

    private static IConfiguration RotationConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun:ApiUrl"] = "http://gluetun.test:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:VpnRestartPauseMs"] = "0",
            // Kein Abruf rotiert von selbst — die Tests loesen den Wechsel gezielt aus.
            ["Crawler:RotateAfterRequests"] = "1000000",
        }).Build();

    [Fact]
    public async Task Rotation_StartAnsweredWithErrorStatus_SendsRecoveryStart()
    {
        // stop kam durch (200), das start beantwortet gluetun mit 500. Frueher wertete der Crawler
        // den Status nicht aus, hielt den Wechsel fuer fertig und schickte kein Recovery-start —
        // der Tunnel blieb bis zur naechsten Rotation unten.
        var putBodies = new List<string>();
        var gluetun = new HttpClient(new RecordingHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            int runningSoFar;
            lock (putBodies)
            {
                putBodies.Add(body);
                runningSoFar = putBodies.Count(b => b.Contains("running"));
            }
            return new HttpResponseMessage(body.Contains("running") && runningSoFar == 1
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK);
        }));
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
        var gate = TestVpnGate.From(factory, RotationConfig());

        Assert.True(await gate.RotateAsync(CancellationToken.None));

        lock (putBodies)
        {
            Assert.Equal(3, putBodies.Count);
            Assert.Contains("stopped", putBodies[0]);
            Assert.Contains("running", putBodies[1]);   // abgelehnt
            Assert.Contains("running", putBodies[2]);   // Recovery-start
        }
    }

    [Fact]
    public async Task Rotation_ControlServerRejects401_IsReportedAsFailed()
    {
        // gluetun-Authentifizierung aktiv, Crawler-Key falsch: beide PUTs liefern 401. Frueher flog
        // keine Exception, der Wechsel galt als gelungen („VPN IP rotated → <alte IP>“), ohne Warnung.
        var gluetun = new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
        var logger = new CapturingLogger<VpnReadinessGate>();
        var gate = new VpnReadinessGate(factory, RotationConfig(), logger);

        await gate.RotateAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("VPN rotation failed"));
        // Der Recovery-start bekommt ebenfalls 401 — er darf sich nicht als gelungen melden.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("per Recovery-start reaktiviert"));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("VPN recovery start failed"));
    }

    [Fact]
    public async Task Rotation_WaitsUntilCrawlRequestInFlightHasFinished()
    {
        // Der Riegel galt nur fuer Drosselung und Wechsel, die Anfrage selbst lief danach ohne ihn.
        // Ein zweiter Aufrufer konnte so den Tunnel unter einer laufenden Anfrage stoppen.
        var events = new List<string>();
        void Record(string e) { lock (events) events.Add(e); }
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var gluetun = new HttpClient(new RecordingHandler(async req =>
        {
            if (req.Method == HttpMethod.Put)
                Record((await req.Content!.ReadAsStringAsync()).Contains("stopped") ? "stop" : "start");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var crawl = new HttpClient(new RecordingHandler(async _ =>
        {
            requestStarted.TrySetResult();
            await releaseRequest.Task;
            Record("request-done");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><h2>T</h2></body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://chess-results.com/tnr1.aspx?lan=0"),
            };
        }));
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
        var config = RotationConfig();
        var gate = TestVpnGate.From(factory, config);
        var service = new CrawlerService(crawl, factory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config, gate);

        var fetch = service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0");
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var rotation = gate.RotateAsync(CancellationToken.None);
        await Task.WhenAny(rotation, Task.Delay(500));
        Assert.False(rotation.IsCompleted, "Der Wechsel darf nicht unter einer laufenden Anfrage stattfinden");
        lock (events) Assert.DoesNotContain("stop", events);

        releaseRequest.SetResult();
        Assert.Contains("<h2>T</h2>", await fetch.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await rotation.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (events) Assert.Equal(new[] { "request-done", "stop", "start" }, events);
    }

    [Fact]
    public async Task FinishedOrFailedRequests_ReleaseTheirInFlightSlot()
    {
        // Eine Anmeldung, die nicht zurueckgegeben wird, liesse jeden folgenden Wechsel die volle
        // Drain-Zeit (25 s) warten. Erfolg, 404, Verbindungsfehler und ein abgelehnter POST.
        const string formPage =
            "<html><body><form>" +
            "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"VS\" />" +
            "<input type=\"hidden\" name=\"__VIEWSTATEGENERATOR\" value=\"VSG\" />" +
            "<input type=\"hidden\" name=\"__EVENTVALIDATION\" value=\"EV\" />" +
            "</form></body></html>";
        var crawl = new HttpClient(new RecordingHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("tnr404"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req });
            if (url.Contains("tnrdown"))
                throw new HttpRequestException("connection refused");
            return Task.FromResult(new HttpResponseMessage(req.Method == HttpMethod.Post
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK)
            {
                Content = new StringContent(formPage, System.Text.Encoding.UTF8, "text/html"),
                RequestMessage = req,
            });
        }));
        var config = RotationConfig();
        var gate = TestVpnGate.Unused();
        var service = new CrawlerService(crawl, Mock.Of<IHttpClientFactory>(), new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config, gate);

        await service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0");
        Assert.Equal(0, gate.InFlightRequests);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.FetchPageAsync("https://chess-results.com/tnr404.aspx?lan=0", "art=0"));
        Assert.Equal(0, gate.InFlightRequests);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.FetchPageAsync("https://chess-results.com/tnrdown.aspx?lan=0", "art=0"));
        Assert.Equal(0, gate.InFlightRequests);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchPlayersAsync("Muster", "Max"));
        Assert.Equal(0, gate.InFlightRequests);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
