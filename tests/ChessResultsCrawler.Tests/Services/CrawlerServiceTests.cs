using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

public class CrawlerServiceTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private static IConfiguration BuildConfig(int crawlMaxAttempts = 1, long? maxResponseBytes = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            // Tests: kein interner Fetch-Retry-Delay und (per Default) kein Crawl-Re-Queue,
            // damit die Verhaltens-Tests schnell bleiben. Backoff zwischen Re-Queues = 0 s.
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = crawlMaxAttempts.ToString(),
            ["Crawler:CrawlRetryBackoffSeconds"] = "0"
        };
        if (maxResponseBytes.HasValue)
            dict["Crawler:MaxResponseBytes"] = maxResponseBytes.Value.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static HttpClient CreateMockHttpClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        return new HttpClient(mockHandler);
    }

    /// <summary>
    /// Creates a CrawlerService with a mock HttpClient that returns the given HTML for any request.
    /// The returned HTML includes a tournament name and round data.
    /// </summary>
    private CrawlerService CreateService(HttpClient httpClient, int crawlMaxAttempts = 1, long? maxResponseBytes = null)
    {
        var parser = new HtmlParserService();
        var logger = Mock.Of<ILogger<CrawlerService>>();
        var httpClientFactory = Mock.Of<IHttpClientFactory>(f =>
            f.CreateClient("Gluetun") == new HttpClient());
        return new CrawlerService(httpClient, httpClientFactory, parser, _db, logger, BuildConfig(crawlMaxAttempts, maxResponseBytes));
    }

    [Fact]
    public async Task ExecuteCrawlAsync_SetsRunningStatus()
    {
        // Arrange: Create a job and a mock that returns valid chess-results responses
        var job = new CrawlJob
        {
            ChessResultsId = "999999",
            JobType = CrawlJobType.CheckNewRounds,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var html = "<html><body><h2>Test Tournament</h2></body></html>";
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/tnr999999.aspx?lan=0")
            }));

        var service = CreateService(httpClient);

        // Act
        var result = await service.ExecuteCrawlAsync(job);

        // Assert: CheckNewRounds doesn't crawl anything, so it should complete
        Assert.Equal(CrawlJobStatus.Completed, result.Status);
        Assert.NotNull(result.StartedAt);
        Assert.NotNull(result.CompletedAt);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_FailedStatus_OnException()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "999998",
            JobType = CrawlJobType.Full,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        // Mock that always fails
        var httpClient = CreateMockHttpClient(_ =>
            throw new HttpRequestException("Connection refused"));

        var service = CreateService(httpClient);

        var result = await service.ExecuteCrawlAsync(job);

        Assert.Equal(CrawlJobStatus.Failed, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_RetriesTransientConnectionError_ThenSucceeds()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "424242",
            JobType = CrawlJobType.CheckNewRounds,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        // Erste zwei HTTP-Aufrufe (erster Anlauf + interner Fetch-Retry) scheitern auf
        // Verbindungsebene, danach klappt es → Re-Queue muss den Crawl noch zum Erfolg bringen.
        var calls = 0;
        var httpClient = CreateMockHttpClient(_ =>
        {
            calls++;
            if (calls <= 2)
                throw new HttpRequestException("Resource temporarily unavailable (chess-results.com:443)");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><h2>Recovered Tournament</h2></body></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/tnr424242.aspx?lan=0")
            });
        });

        var service = CreateService(httpClient, crawlMaxAttempts: 3);
        var result = await service.ExecuteCrawlAsync(job);

        Assert.Equal(CrawlJobStatus.Completed, result.Status);
        Assert.True(calls >= 3, $"expected a re-queued attempt, only {calls} HTTP calls");
    }

    [Fact]
    public async Task ExecuteCrawlAsync_TransientError_FailsAfterMaxAttempts()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "434343",
            JobType = CrawlJobType.CheckNewRounds,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var httpClient = CreateMockHttpClient(_ =>
            throw new HttpRequestException("Resource temporarily unavailable (chess-results.com:443)"));

        var service = CreateService(httpClient, crawlMaxAttempts: 2);
        var result = await service.ExecuteCrawlAsync(job);

        Assert.Equal(CrawlJobStatus.Failed, result.Status);
        Assert.Contains("temporarily unavailable", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_CreatesTournament_WhenNotExists()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "123456",
            JobType = CrawlJobType.CheckNewRounds,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var html = "<html><body><h2>My Chess Tournament</h2></body></html>";
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/tnr123456.aspx?lan=0")
            }));

        var service = CreateService(httpClient);
        await service.ExecuteCrawlAsync(job);

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.ChessResultsId == "123456");
        Assert.NotNull(tournament);
        Assert.Equal("My Chess Tournament", tournament.Name);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_UpdatesExistingTournament()
    {
        var existing = new Tournament
        {
            ChessResultsId = "111111",
            Name = "Old Name",
            BaseUrl = "https://chess-results.com/old",
            SNode = "s1"
        };
        _db.Tournaments.Add(existing);
        await _db.SaveChangesAsync();

        var job = new CrawlJob
        {
            ChessResultsId = "111111",
            JobType = CrawlJobType.CheckNewRounds,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var html = "<html><body><h2>New Name</h2></body></html>";
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/s2/tnr111111.aspx?lan=0")
            }));

        var service = CreateService(httpClient);
        await service.ExecuteCrawlAsync(job);

        var tournament = await _db.Tournaments.FirstAsync(t => t.ChessResultsId == "111111");
        Assert.Equal("s2", tournament.SNode);
        Assert.NotNull(tournament.UpdatedAt);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_SSRF_RejectsNonChessResultsDomain()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "777777",
            JobType = CrawlJobType.Full,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        // Mock that returns a redirect to evil domain
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://evil-domain.com/steal-data")
            }));

        var service = CreateService(httpClient);
        var result = await service.ExecuteCrawlAsync(job);

        Assert.Equal(CrawlJobStatus.Failed, result.Status);
        Assert.Contains("unexpected domain", result.ErrorMessage);
    }

    [Theory]
    [InlineData("https://chess-results.com/tnr1.aspx?lan=0", "art=2", "https://chess-results.com/tnr1.aspx?lan=0&art=2")]
    [InlineData("https://chess-results.com/tnr1.aspx", "art=0", "https://chess-results.com/tnr1.aspx?art=0")]
    public async Task FetchPageAsync_ConstructsUrlCorrectly(string baseUrl, string queryParams, string expectedUrl)
    {
        string? capturedUrl = null;
        var httpClient = CreateMockHttpClient(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>")
            });
        });

        var service = CreateService(httpClient);
        await service.FetchPageAsync(baseUrl, queryParams);

        Assert.Equal(expectedUrl, capturedUrl);
    }

    [Fact]
    public async Task ExecuteCrawlAsync_SetsJobTournamentId()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "555555",
            JobType = CrawlJobType.CheckNewRounds,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var html = "<html><body><h2>Tournament 555</h2></body></html>";
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/tnr555555.aspx?lan=0")
            }));

        var service = CreateService(httpClient);
        await service.ExecuteCrawlAsync(job);

        Assert.True(job.TournamentId > 0);
    }

    [Fact]
    public async Task SearchPlayersAsync_CancelledToken_Throws()
    {
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>")
            }));
        var service = CreateService(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchPlayersAsync("Muster", null, cts.Token));
    }

    [Fact]
    public async Task SearchPlayerTournamentsAsync_CancelledToken_Throws()
    {
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>")
            }));
        var service = CreateService(httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchPlayerTournamentsAsync("Muster", null, cts.Token));
    }

    [Fact]
    public async Task ExecuteCrawlAsync_PlayersOnly_PersistsPlayersAndTeams()
    {
        // Deckt den (auf IsRelational gegateten) Team-/Spieler-Upsert ab: auf der InMemory-DB
        // läuft er ohne echte Transaktion, muss aber weiterhin Spieler + Teams korrekt anlegen.
        var job = new CrawlJob
        {
            ChessResultsId = "777777",
            JobType = CrawlJobType.PlayersOnly,
            Status = CrawlJobStatus.Queued
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        const string playerTable =
            "<html><body><table>" +
            "<tr><th>Nr.</th><th>Title</th><th>Name</th><th>FideID</th><th>Rtg</th><th>FED</th><th>Team</th><th>Br.</th></tr>" +
            "<tr><td>1</td><td>GM</td><td>Carlsen, Magnus</td><td>1503014</td><td>2830</td><td>NOR</td><td>Team A</td><td>1</td></tr>" +
            "<tr><td>2</td><td>IM</td><td>Doe, John</td><td>1234567</td><td>2450</td><td>GER</td><td>Team A</td><td>2</td></tr>" +
            "</table></body></html>";

        var httpClient = CreateMockHttpClient(req =>
        {
            var url = req.RequestUri!.ToString();
            // art=16 = Spielerliste; sonst die Turnierseite (Name/Details).
            var html = url.Contains("art=16")
                ? playerTable
                : "<html><body><h2>Player Tournament</h2></body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/tnr777777.aspx?lan=0")
            });
        });

        var service = CreateService(httpClient);
        var result = await service.ExecuteCrawlAsync(job);

        Assert.Equal(CrawlJobStatus.Completed, result.Status);
        var players = _db.Players.Where(p => p.TournamentId == job.TournamentId).ToList();
        Assert.Equal(2, players.Count);
        var teams = _db.Teams.Where(t => t.TournamentId == job.TournamentId).ToList();
        Assert.Single(teams);
        Assert.Equal("Team A", teams[0].Name);
    }

    [Fact]
    public async Task FetchPageAsync_OversizedResponse_ThrowsInsteadOfBuffering()
    {
        // Antwort weit über dem (klein gesetzten) Limit → sauberer Abbruch statt OOM.
        var oversized = new string('A', 4096);
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversized),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/tnr1.aspx?lan=0")
            }));
        var service = CreateService(httpClient, maxResponseBytes: 1024);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0"));
        Assert.Contains("maximum allowed size", ex.Message);
    }

    [Fact]
    public async Task FetchPageAsync_ResponseWithinLimit_ReturnsBody()
    {
        var body = new string('B', 512);
        var httpClient = CreateMockHttpClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "https://chess-results.com/tnr1.aspx?lan=0")
            }));
        var service = CreateService(httpClient, maxResponseBytes: 1024);

        var result = await service.FetchPageAsync("https://chess-results.com/tnr1.aspx?lan=0", "art=0");
        Assert.Equal(body, result);
    }

    /// <summary>
    /// Custom HttpMessageHandler for mocking HttpClient
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request);
    }
}
