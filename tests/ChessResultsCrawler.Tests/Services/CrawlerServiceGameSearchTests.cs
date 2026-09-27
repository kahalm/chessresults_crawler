using System.Net;
using System.Net.Http.Headers;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>Partiedatenbank per FIDE-ID (LeagueHub): Formularfelder, PGN-Antwort, „keine Partien".</summary>
public class CrawlerServiceGameSearchTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();

    private const string FormPage = "<html><body><form>" +
        "<input type=\"hidden\" name=\"__VIEWSTATE\" id=\"__VIEWSTATE\" value=\"VS\" />" +
        "<input type=\"hidden\" name=\"__VIEWSTATEGENERATOR\" id=\"__VIEWSTATEGENERATOR\" value=\"VSG\" />" +
        "<input type=\"hidden\" name=\"__EVENTVALIDATION\" id=\"__EVENTVALIDATION\" value=\"EV\" />" +
        "</form></body></html>";

    private (CrawlerService, Func<Dictionary<string, string>?>) Create(string postBody, string mediaType)
    {
        Dictionary<string, string>? form = null;
        var handler = new Handler(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var body = await req.Content!.ReadAsStringAsync();
                form = body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(
                    p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
                var c = new StringContent(postBody);
                c.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = c, RequestMessage = req };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FormPage), RequestMessage = req };
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Crawler:MinDelayMs"] = "0", ["Crawler:RetryDelayMs"] = "0", ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:CrawlRetryBackoffSeconds"] = "0",
        }).Build();
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        var svc = new CrawlerService(new HttpClient(handler), factory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.Unused());
        return (svc, () => form);
    }

    [Fact]
    public async Task PostsFideId_AndTheGermanDownloadButton()
    {
        var pgn = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "league-games-1606921.pgn"));
        var (svc, form) = Create(pgn, "text/plain");
        var result = await svc.SearchGamesPgnByFideAsync("1606921");
        Assert.StartsWith("[Event ", result);
        Assert.Equal("1606921", form()!["ctl00$P1$Txt_FideID"]);
        // Der Knopf-WERT muss exakt die deutsche Beschriftung sein, sonst antwortet ASP.NET mit „Laufzeitfehler"
        Assert.Equal("Download als PGN-Datei", form()!["ctl00$P1$cb_DownLoadPGN"]);
        Assert.Equal("-", form()!["ctl00$P1$combo_spielerfarbe"]);
        Assert.Equal("VS", form()!["__VIEWSTATE"]);
    }

    [Fact]
    public async Task HtmlAnswer_MeansNoGames()
    {
        var (svc, _) = Create("<html><body>keine Partien</body></html>", "text/html");
        Assert.Equal("", await svc.SearchGamesPgnByFideAsync("1"));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => f(request);
    }
}
