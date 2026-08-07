using System.Net;
using System.Text;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Gesendet wird mit HttpCompletionOption.ResponseHeadersRead — die Verbindung gehört der Response
/// bis zum Dispose. Flog EnsureSuccessStatusCode VOR dem Body-Read (500/429-Phasen von
/// chess-results), blieb die Response undisposed und die Verbindung bis zur GC-Finalisierung
/// belegt. Dieser Test hält die Reihenfolge/das using fest.
/// </summary>
public class CrawlerServiceResponseDisposeTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceResponseDisposeTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Content, der sein Dispose meldet — Stellvertreter für die belegte Verbindung.</summary>
    private sealed class TrackingContent : HttpContent
    {
        private readonly byte[] _bytes;
        public bool Disposed { get; private set; }
        public TrackingContent(string body) => _bytes = Encoding.UTF8.GetBytes(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = _handler(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private CrawlerService CreateService(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
        }).Build();
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        return new CrawlerService(new HttpClient(handler), factory, new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config);
    }

    [Fact]
    public async Task SearchPlayersAsync_ErrorResponse_DisposesResponse()
    {
        var errorContent = new TrackingContent("<html>500</html>");
        var svc = CreateService(new Handler(req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><form></form></html>") }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = errorContent }));

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.SearchPlayersAsync("Mueller", null));

        Assert.True(errorContent.Disposed, "Fehler-Response wurde nicht disposed → Verbindung bleibt belegt");
    }

    [Fact]
    public async Task SearchPlayerTournamentsAsync_ErrorResponse_DisposesResponse()
    {
        var errorContent = new TrackingContent("<html>500</html>");
        var svc = CreateService(new Handler(req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><form></form></html>") }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = errorContent }));

        await Assert.ThrowsAsync<HttpRequestException>(() => svc.SearchPlayerTournamentsAsync("Mueller", null));

        Assert.True(errorContent.Disposed, "Fehler-Response wurde nicht disposed → Verbindung bleibt belegt");
    }

    [Fact]
    public async Task FetchHtmlAsync_SuccessResponse_DisposesResponse()
    {
        var okContent = new TrackingContent("OK-BODY");
        var svc = CreateService(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = okContent }));

        var body = await svc.FetchHtmlAsync("https://chess-results.com/x");

        Assert.Equal("OK-BODY", body);
        Assert.True(okContent.Disposed);
    }
}
