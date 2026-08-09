using System.Diagnostics;
using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert die HTTP-Retry-Strategie der Fetches ab: 429/5xx werden bis zu 3-mal versucht,
/// die Wartezeit kommt aus dem Retry-After-Header (Sekunden oder HTTP-Datum), sonst
/// exponentiell aus RetryDelayMs. Andere 4xx (404 …) scheitern sofort ohne Retry.
/// </summary>
public class CrawlerServiceHttpRetryTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceHttpRetryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private CrawlerService CreateService(SequenceHandler handler, int retryDelayMs = 0)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = retryDelayMs.ToString(),
            ["Crawler:MinDelayMs"] = "0",
            // Statischer Request-Zaehler: keine VPN-Rotation mitten im Test ausloesen.
            ["Crawler:RotateAfterRequests"] = "1000000",
            ["Crawler:VpnRestartPauseMs"] = "0",
        }).Build();
        var httpClientFactory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        return new CrawlerService(new HttpClient(handler), httpClientFactory, new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? retryAfter = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("err") };
        if (retryAfter is not null)
            r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return r;
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task FetchHtml_429ThenSuccess_ReturnsBodyOnThirdAttempt()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.TooManyRequests),
            () => Status(HttpStatusCode.TooManyRequests),
            () => Ok("OK-BODY"));
        var svc = CreateService(handler);

        var body = await svc.FetchHtmlAsync("https://chess-results.com/x");

        Assert.Equal("OK-BODY", body);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task FetchHtml_Persistent429_StopsAfterThreeAttempts_ThenThrows()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.TooManyRequests));
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.FetchHtmlAsync("https://chess-results.com/x"));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task FetchHtml_500ThenSuccess_Retries()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.InternalServerError),
            () => Ok("RECOVERED"));
        var svc = CreateService(handler);

        var body = await svc.FetchHtmlAsync("https://chess-results.com/x");

        Assert.Equal("RECOVERED", body);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchHtml_404_FailsFast_WithoutRetry()
    {
        var handler = new SequenceHandler(() => Status(HttpStatusCode.NotFound));
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.FetchHtmlAsync("https://chess-results.com/x"));
        // Ein 404 wird durch Wiederholen nicht besser → genau EIN Request.
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchHtml_HonorsRetryAfterSecondsHeader()
    {
        // RetryDelayMs=0 → der exponentielle Fallback wuerde SOFORT erneut anfragen.
        // Eine messbare Wartezeit kann also nur vom Retry-After-Header (1 s) kommen.
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.TooManyRequests, retryAfter: "1"),
            () => Ok("AFTER-WAIT"));
        var svc = CreateService(handler, retryDelayMs: 0);

        var sw = Stopwatch.StartNew();
        var body = await svc.FetchHtmlAsync("https://chess-results.com/x");
        sw.Stop();

        Assert.Equal("AFTER-WAIT", body);
        Assert.Equal(2, handler.RequestCount);
        Assert.True(sw.ElapsedMilliseconds >= 800,
            $"Retry-After: 1 wurde nicht abgewartet (nur {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task FetchWithRedirect_429ThenSuccess_ReturnsFinalUrlAndBody()
    {
        var handler = new SequenceHandler(
            () => Status(HttpStatusCode.TooManyRequests),
            () => Ok("VIA-REDIRECT-API"));
        var svc = CreateService(handler);

        var (url, html) = await svc.FetchWithRedirectAsync("https://chess-results.com/x");

        Assert.Equal("https://chess-results.com/x", url);
        Assert.Equal("VIA-REDIRECT-API", html);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void IsRetryableStatus_Only429And5xx(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, CrawlerService.IsRetryableStatus(status));
    }

    [Fact]
    public void GetRetryAfterDelay_ParsesDeltaSeconds()
    {
        var delay = CrawlerService.GetRetryAfterDelay(
            Status(HttpStatusCode.TooManyRequests, retryAfter: "7"), DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.FromSeconds(7), delay);
    }

    [Fact]
    public void GetRetryAfterDelay_ParsesHttpDate()
    {
        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var delay = CrawlerService.GetRetryAfterDelay(
            Status(HttpStatusCode.ServiceUnavailable, retryAfter: now.AddSeconds(30).ToString("R")), now);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void GetRetryAfterDelay_PastHttpDate_YieldsZero()
    {
        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var delay = CrawlerService.GetRetryAfterDelay(
            Status(HttpStatusCode.ServiceUnavailable, retryAfter: now.AddSeconds(-30).ToString("R")), now);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void GetRetryAfterDelay_CapsExcessiveValues()
    {
        // "Retry-After: 86400" (1 Tag) darf keinen Crawl stundenlang schlafen legen.
        var delay = CrawlerService.GetRetryAfterDelay(
            Status(HttpStatusCode.TooManyRequests, retryAfter: "86400"), DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Theory]
    [InlineData(null)]        // kein Header
    [InlineData("kaputt")]    // nicht parsebar → wie kein Header
    public void GetRetryAfterDelay_MissingOrInvalidHeader_ReturnsNull(string? headerValue)
    {
        var delay = CrawlerService.GetRetryAfterDelay(
            Status(HttpStatusCode.TooManyRequests, retryAfter: headerValue), DateTimeOffset.UtcNow);
        Assert.Null(delay);
    }

    /// <summary>Liefert pro Request die naechste Factory; die letzte wird wiederholt.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>[] _responses;
        public int RequestCount { get; private set; }

        public SequenceHandler(params Func<HttpResponseMessage>[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var idx = Math.Min(RequestCount, _responses.Length - 1);
            RequestCount++;
            var resp = _responses[idx]();
            resp.RequestMessage = request;
            return Task.FromResult(resp);
        }
    }
}
