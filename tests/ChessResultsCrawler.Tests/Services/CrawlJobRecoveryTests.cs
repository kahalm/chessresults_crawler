using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Services;

public class CrawlJobRecoveryTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlJobRecoveryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RecoverStaleJobs_MarksQueuedAndRunningAsFailed_LeavesFinishedUntouched()
    {
        _db.CrawlJobs.AddRange(
            new CrawlJob { ChessResultsId = "1", Status = CrawlJobStatus.Queued },
            new CrawlJob { ChessResultsId = "2", Status = CrawlJobStatus.Running },
            new CrawlJob { ChessResultsId = "3", Status = CrawlJobStatus.Completed },
            new CrawlJob { ChessResultsId = "4", Status = CrawlJobStatus.Failed });
        await _db.SaveChangesAsync();

        var count = await CrawlJobRecovery.RecoverStaleJobsAsync(_db);

        Assert.Equal(2, count);
        var byId = await _db.CrawlJobs.ToDictionaryAsync(j => j.ChessResultsId);
        Assert.Equal(CrawlJobStatus.Failed, byId["1"].Status);
        Assert.Equal(CrawlJobStatus.Failed, byId["2"].Status);
        Assert.Equal(CrawlJobRecovery.StaleMessage, byId["1"].ErrorMessage);
        Assert.NotNull(byId["1"].CompletedAt);
        // Bereits abgeschlossene Jobs bleiben unangetastet.
        Assert.Equal(CrawlJobStatus.Completed, byId["3"].Status);
        Assert.Equal(CrawlJobStatus.Failed, byId["4"].Status);
        Assert.Null(byId["3"].ErrorMessage);
    }

    [Fact]
    public async Task RecoverStaleJobs_NoStaleJobs_ReturnsZero()
    {
        _db.CrawlJobs.Add(new CrawlJob { ChessResultsId = "1", Status = CrawlJobStatus.Completed });
        await _db.SaveChangesAsync();

        Assert.Equal(0, await CrawlJobRecovery.RecoverStaleJobsAsync(_db));
    }
}
