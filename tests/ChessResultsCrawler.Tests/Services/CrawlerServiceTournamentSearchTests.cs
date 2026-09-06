using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Der POST-Teil der Turniersuche. Die Feldnamen sind gegen die echte Seite verifiziert - faellt
/// hier etwas um, hat entweder chess-results das Formular umgebaut oder jemand hat einen Namen
/// vertippt; beides wuerde sonst nur als stille leere Trefferliste auffallen.
/// </summary>
public class CrawlerServiceTournamentSearchTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceTournamentSearchTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SearchTournamentsAsync_PostsExpectedFormFields_ToResolvedNodeUrl()
    {
        var (service, captured) = CreateService();

        var result = await service.SearchTournamentsAsync(
            "AUT", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31));

        Assert.NotEmpty(result);
        Assert.NotNull(captured.Form);

        // POST geht auf die aufgeloeste Node-URL zurueck, nicht auf chess-results.com -
        // sonst passt der ViewState nicht zur Node und die Suche liefert nichts.
        Assert.Equal("https://s2.chess-results.com/TurnierSuche.aspx?lan=1&SNode=S0", captured.PostUrl);

        Assert.Equal("AUT", captured.Form!["ctl00$P1$combo_land"]);
        Assert.Equal("01.09.2026", captured.Form["ctl00$P1$txt_von_tag"]);
        Assert.Equal("31.12.2026", captured.Form["ctl00$P1$txt_bis_tag"]);
        Assert.Equal("5", captured.Form["ctl00$P1$combo_art"]);          // alle Turnierarten
        Assert.Equal("0", captured.Form["ctl00$P1$combo_bedenkzeit"]);   // alle Bedenkzeiten
        Assert.Equal("3", captured.Form["ctl00$P1$combo_sort"]);         // nach Start-Datum
        Assert.Equal("5", captured.Form["ctl00$P1$combo_anzahl_zeilen"]);// 2000 Zeilen
        Assert.Equal("Search", captured.Form["ctl00$P1$cb_suchen"]);
        Assert.Equal("VS-TOKEN", captured.Form["__VIEWSTATE"]);
        Assert.Equal("EV-TOKEN", captured.Form["__EVENTVALIDATION"]);
        Assert.Equal("VSG-TOKEN", captured.Form["__VIEWSTATEGENERATOR"]);
    }

    [Fact]
    public async Task SearchTournamentsAsync_MaxRows_PicksSmallestDropdownStepAndTruncates()
    {
        var (service, captured) = CreateService();

        var result = await service.SearchTournamentsAsync(
            "GER", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), maxRows: 2);

        Assert.Equal("0", captured.Form!["ctl00$P1$combo_anzahl_zeilen"]); // 2 -> Stufe 100
        Assert.Equal(2, result.Count);                                     // Fixture hat 6 Zeilen
    }

    [Fact]
    public async Task SearchTournamentsAsync_NonChessResultsRedirect_IsRejected()
    {
        // SSRF-Schutz: eine Weiterleitung auf einen fremden Host darf nicht gefolgt werden.
        var handler = new CapturingHandler(_ => Task.FromResult(Redirect("https://evil.example.com/x")));
        var service = Build(new HttpClient(handler));

        await Assert.ThrowsAnyAsync<Exception>(() => service.SearchTournamentsAsync(
            "AUT", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));
    }

    [Theory]
    [InlineData(1, "0")]
    [InlineData(100, "0")]
    [InlineData(101, "1")]
    [InlineData(250, "1")]
    [InlineData(500, "2")]
    [InlineData(1000, "3")]
    [InlineData(1500, "4")]
    [InlineData(2000, "5")]
    [InlineData(99999, "5")]
    public void RowCountOption_MapsToSmallestCoveringStep(int maxRows, string expected)
        => Assert.Equal(expected, CrawlerService.RowCountOption(maxRows));

    // ---------------------------------------------------------------------

    private (CrawlerService Service, CapturingHandler Captured) CreateService()
    {
        var formPage =
            "<html><body><form>" +
            "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"VS-TOKEN\" />" +
            "<input type=\"hidden\" name=\"__VIEWSTATEGENERATOR\" value=\"VSG-TOKEN\" />" +
            "<input type=\"hidden\" name=\"__EVENTVALIDATION\" value=\"EV-TOKEN\" />" +
            "</form></body></html>";
        var resultPage = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "tournament-search-en.html"));

        var handler = new CapturingHandler(async req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.Host == "chess-results.com")
                return Redirect("https://s2.chess-results.com/TurnierSuche.aspx?lan=1&SNode=S0");
            if (req.Method == HttpMethod.Get)
                return Ok(formPage);
            return await Task.FromResult(Ok(resultPage));
        });

        return (Build(new HttpClient(handler)), handler);
    }

    private CrawlerService Build(HttpClient httpClient)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Ohne das schliefe jeder Test 1,5 s im globalen Rate-Limiter.
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:CrawlRetryBackoffSeconds"] = "0",
        }).Build();

        var httpClientFactory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        return new CrawlerService(httpClient, httpClientFactory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config);
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        public Dictionary<string, string>? Form { get; private set; }
        public string? PostUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.Content is not null)
            {
                PostUrl = request.RequestUri!.ToString();
                var body = await request.Content.ReadAsStringAsync(ct);
                Form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .ToDictionary(
                        p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
                        p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
            }
            var response = await _handler(request);
            // Ein echter HttpMessageHandler haengt die Request-Nachricht an die Antwort; darueber
            // ermittelt FetchWithRetriesAsync die aufgeloeste Node-URL. Ohne das faellt der Code
            // auf die Ausgangs-URL zurueck und der Test wuerde am falschen Verhalten vorbeilaufen.
            response.RequestMessage ??= request;
            return response;
        }
    }
}
