using System.Net;
using ChessResultsCrawler.Controllers;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Eingabepruefung des Suchendpunkts. Die Werte gehen unveraendert in ein fremdes Formular - eine
/// ungeprueft durchgereichte Foederation oder ein absurdes Zeitfenster laesst chess-results
/// arbeiten, nicht uns.
/// </summary>
public class TournamentSearchControllerTests : IDisposable
{
    private readonly AppDbContext _db;

    public TournamentSearchControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("AUSTRIA")]
    [InlineData("AU1")]
    [InlineData("AUT'; DROP")]
    public async Task Search_InvalidFederation_ReturnsBadRequest(string fed)
    {
        var result = await CreateController().Search(fed, "2026-09-01", "2026-12-31");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("01.09.2026", "2026-12-31")]
    [InlineData("2026-09-01", "31.12.2026")]
    [InlineData("morgen", "2026-12-31")]
    [InlineData("", "2026-12-31")]
    public async Task Search_NonIsoDates_ReturnBadRequest(string from, string to)
    {
        var result = await CreateController().Search("AUT", from, to);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_EndBeforeStart_ReturnsBadRequest()
    {
        var result = await CreateController().Search("AUT", "2026-12-31", "2026-09-01");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_WindowLargerThanThreeYears_ReturnsBadRequest()
    {
        var result = await CreateController().Search("AUT", "2020-01-01", "2030-01-01");
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_ValidRequest_ReturnsMappedEntries()
    {
        var before = DateTime.UtcNow;

        var result = await CreateController().Search("aut", "2026-09-01", "2026-12-31");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsType<List<DirectoryTournamentResponse>>(ok.Value);
        Assert.Equal(6, entries.Count);

        var first = entries[0];
        Assert.Equal("1457129", first.ChessResultsId);
        Assert.Equal("2026-12-18", first.StartDate);
        Assert.Equal("2026-12-20", first.EndDate);
        Assert.Equal("Ranshofen", first.Location);
        Assert.Equal("16 Days", first.LastUpdateText);
        // 16 Tage alt: der abgeleitete Zeitpunkt muss vor dem Aufruf liegen.
        Assert.NotNull(first.LastUpdatedApproxUtc);
        Assert.True(first.LastUpdatedApproxUtc < before.AddDays(-15));
    }

    private TournamentSearchController CreateController()
    {
        var formPage =
            "<html><body><form>" +
            "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"VS\" />" +
            "<input type=\"hidden\" name=\"__VIEWSTATEGENERATOR\" value=\"VSG\" />" +
            "<input type=\"hidden\" name=\"__EVENTVALIDATION\" value=\"EV\" />" +
            "</form></body></html>";
        var resultPage = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "tournament-search-en.html"));

        var handler = new StubHandler(req => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(req.Method == HttpMethod.Post ? resultPage : formPage)
            }));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:CrawlRetryBackoffSeconds"] = "0",
        }).Build();

        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        var crawler = new CrawlerService(new HttpClient(handler), factory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config);

        return new TournamentSearchController(crawler);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await _handler(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
