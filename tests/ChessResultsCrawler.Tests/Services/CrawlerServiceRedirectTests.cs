using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert das MANUELLE Redirect-Folgen (SSRF-Schutz vor dem Request) ab: erlaubte
/// chess-results.com-Redirects werden gefolgt, fremde/http-Ziele werden abgewiesen BEVOR der
/// Request an sie rausgeht — und das gilt auch für die POST-Antwort der Spielersuche.
/// </summary>
public class CrawlerServiceRedirectTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceRedirectTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private CrawlerService CreateService(RecordingHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:CrawlRetryBackoffSeconds"] = "0",
        }).Build();
        var httpClientFactory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        return new CrawlerService(new HttpClient(handler), httpClientFactory, new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config);
    }

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return r;
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task FetchHtml_FollowsAllowedRedirectToChessResults_AndReturnsFinalBody()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsolutePath == "/start"
                ? Redirect(HttpStatusCode.Found, "https://chess-results.com/final")
                : Ok("FINAL-BODY"));
        var svc = CreateService(handler);

        var body = await svc.FetchHtmlAsync("https://chess-results.com/start");

        Assert.Equal("FINAL-BODY", body);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://chess-results.com/final", handler.Requests[1].ToString());
    }

    [Fact]
    public async Task FetchHtml_RejectsRedirectToForeignHost_WithoutIssuingTheInternalRequest()
    {
        // Bewusst https, damit der .NET-Downgrade-Schutz NICHT greift — nur unser Host-Guard schützt.
        var handler = new RecordingHandler(_ => Redirect(HttpStatusCode.Found, "https://internal.local/secret"));
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchHtmlAsync("https://chess-results.com/start"));
        // Kernaussage: der Request an den internen Host ist NIE rausgegangen.
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, u => u.Host == "internal.local");
    }

    [Fact]
    public async Task FetchHtml_RejectsHttpsToHttpDowngradeRedirect()
    {
        var handler = new RecordingHandler(_ => Redirect(HttpStatusCode.Found, "http://chess-results.com/x"));
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchHtmlAsync("https://chess-results.com/start"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchHtml_ResolvesRelativeLocationAgainstCurrentHost()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsolutePath == "/start"
                ? Redirect(HttpStatusCode.Found, "/Tnr.aspx?x=1")   // relativ
                : Ok("REL-OK"));
        var svc = CreateService(handler);

        var body = await svc.FetchHtmlAsync("https://chess-results.com/start");

        Assert.Equal("REL-OK", body);
        Assert.Equal("https://chess-results.com/Tnr.aspx?x=1", handler.Requests[1].ToString());
    }

    [Fact]
    public async Task FetchHtml_ThrowsOnRedirectLoopBeyondCap()
    {
        // Immer ein (erlaubter) Redirect → Kette muss beim Hop-Cap abbrechen, nicht endlos laufen.
        var handler = new RecordingHandler(_ => Redirect(HttpStatusCode.Found, "https://chess-results.com/next"));
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchHtmlAsync("https://chess-results.com/start"));
        Assert.True(handler.Requests.Count <= 12, $"zu viele Hops: {handler.Requests.Count}");
    }

    [Fact]
    public async Task SearchPlayers_RejectsForeignRedirectOnThePostResponse()
    {
        // GET der Formularseite liefert 200 (ohne ViewState-Felder → "" Fallback); der POST antwortet
        // mit einem 302 auf einen fremden Host → muss abgewiesen werden, bevor er rausgeht.
        var handler = new RecordingHandler(req => req.Method == HttpMethod.Post
            ? Redirect(HttpStatusCode.Found, "https://internal.local/exfil")
            : Ok("<html><body>form</body></html>"));
        var svc = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SearchPlayersAsync("Mustermann", null));
        Assert.DoesNotContain(handler.Requests, u => u.Host == "internal.local");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<Uri> Requests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var resp = _responder(request);
            resp.RequestMessage = request;
            return Task.FromResult(resp);
        }
    }
}
