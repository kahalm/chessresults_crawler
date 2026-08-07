using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert ab, dass der rounds/check-Pfad das CancellationToken durchreicht: bricht der aufrufende
/// RookHub-Proxy ab (30-s-Timeout, Poll alle 30 s je Monitor), darf kein verwaister Fetch mehr
/// rausgehen und am globalen Rate-Limiter (bis 60 s) hängen — sonst stauen sich Zombie-Requests
/// und blockieren echte Crawls.
/// </summary>
public class RoundDetectionServiceTests : IDisposable
{
    private readonly AppDbContext _db;

    public RoundDetectionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body></body></html>"),
                RequestMessage = request,
            });
        }
    }

    [Fact]
    public async Task CheckForNewRoundsAsync_CancelledToken_DoesNotFetch()
    {
        var tournament = new Tournament { Id = 1, ChessResultsId = "1", Name = "T", TotalRounds = 9 };
        var handler = new CountingHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
        }).Build();
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        var crawler = new CrawlerService(new HttpClient(handler), factory, new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config);
        var sut = new RoundDetectionService(crawler, new HtmlParserService(), _db,
            new MemoryCache(new MemoryCacheOptions()));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.CheckForNewRoundsAsync(tournament, cts.Token));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CheckForNewRoundsAsync_ReturnsCachedResultWithoutSecondFetch()
    {
        var tournament = new Tournament { Id = 2, ChessResultsId = "2", Name = "T", TotalRounds = 0 };
        var handler = new CountingHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
        }).Build();
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        var crawler = new CrawlerService(new HttpClient(handler), factory, new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config);
        var sut = new RoundDetectionService(crawler, new HtmlParserService(), _db,
            new MemoryCache(new MemoryCacheOptions()));

        var first = await sut.CheckForNewRoundsAsync(tournament, CancellationToken.None);
        var second = await sut.CheckForNewRoundsAsync(tournament, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, handler.Requests);
    }
}
