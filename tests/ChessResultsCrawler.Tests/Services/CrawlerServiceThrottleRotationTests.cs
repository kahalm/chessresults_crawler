using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// S2-011: Bleibt ein Abruf nach allen Fetch-Versuchen auf 429 (Ausgang gedrosselt), wechselt der
/// Crawl-Auftrag EINMAL den VPN-Ausgang und setzt neu an; frueher endete er sofort als Failed.
/// Beim zweiten erschoepften 429 bleibt es bei Failed. Der Fetch selbst gibt weiterhin nach drei
/// 429 auf (CrawlerServiceHttpRetryTests.FetchHtml_Persistent429_StopsAfterThreeAttempts_ThenThrows).
/// </summary>
public class CrawlerServiceThrottleRotationTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceThrottleRotationTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose() => _db.Dispose();

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var r = respond(request);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }

    private const string Details = "<html><body><h2>Open</h2><table>" +
                                   "<tr><td class='CR'>Rundenanzahl</td><td class='CR'>5</td></tr></table></body></html>";

    private async Task<(CrawlJob Job, int Puts, int CrawlCalls)> RunAsync(int throttledRequests)
    {
        var puts = 0;
        var gluetun = new HttpClient(new FuncHandler(req =>
        {
            if (req.Method == HttpMethod.Put) Interlocked.Increment(ref puts);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == gluetun);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun:ApiUrl"] = "http://gluetun.test:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:VpnRestartPauseMs"] = "0",
            ["Crawler:RotateAfterRequests"] = "1000000",
            ["Crawler:CrawlRetryBackoffSeconds"] = "0",
        }).Build();

        var crawlCalls = 0;
        var crawl = new FuncHandler(_ =>
        {
            var n = Interlocked.Increment(ref crawlCalls);
            return n <= throttledRequests
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("<html>slow down</html>") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Details) };
        });
        var svc = new CrawlerService(new HttpClient(crawl), new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.From(factory, config));

        var job = new CrawlJob { ChessResultsId = "4290", JobType = CrawlJobType.PlayersOnly, Status = CrawlJobStatus.Queued };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();
        var done = await svc.ExecuteCrawlAsync(job);
        return (done, puts, crawlCalls);
    }

    [Fact]
    public async Task ThreeTimes429_RotatesOnce_AndCompletesOverNewExit()
    {
        var (job, puts, _) = await RunAsync(throttledRequests: 3);

        Assert.Equal(CrawlJobStatus.Completed, job.Status);
        Assert.Equal(2, puts);   // genau ein Wechsel: stop + start
    }

    [Fact]
    public async Task StillThrottledAfterRotation_FailsWithoutSecondRotation()
    {
        var (job, puts, crawlCalls) = await RunAsync(throttledRequests: int.MaxValue);

        Assert.Equal(CrawlJobStatus.Failed, job.Status);
        Assert.Contains("429", job.ErrorMessage);
        Assert.Equal(2, puts);        // nur EIN Wechsel je Auftrag
        Assert.Equal(6, crawlCalls);  // 3 Versuche vor, 3 nach dem Wechsel
    }
}
