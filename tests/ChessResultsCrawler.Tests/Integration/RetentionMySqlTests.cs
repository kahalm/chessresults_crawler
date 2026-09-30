using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Integration;

/// <summary>
/// Der Löschlauf gegen eine echte MariaDB mit dem migrierten Schema: dort läuft der
/// ExecuteDelete-Zweig (InMemory nimmt RemoveRange), samt der berechneten ActiveKey-Spalte.
/// </summary>
public class RetentionMySqlTests
{
    [MySqlFact]
    public async Task Purge_OnMariaDb_DeletesOnlyExpiredRows()
    {
        var name = "crawler_it_" + Guid.NewGuid().ToString("N")[..12];
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql($"{MySqlFactAttribute.ConnectionBase};database={name}", new MariaDbServerVersion(new Version(11, 0, 0)))
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        try
        {
            var now = DateTime.UtcNow;
            db.CrawlJobs.AddRange(
                new CrawlJob { ChessResultsId = "1", Status = CrawlJobStatus.Completed, CreatedAt = now.AddDays(-40), CompletedAt = now.AddDays(-31) },
                new CrawlJob { ChessResultsId = "2", Status = CrawlJobStatus.Failed, CreatedAt = now.AddDays(-45) },
                new CrawlJob { ChessResultsId = "3", Status = CrawlJobStatus.Completed, CreatedAt = now.AddDays(-40), CompletedAt = now.AddDays(-29) },
                new CrawlJob { ChessResultsId = "4", Status = CrawlJobStatus.Queued, CreatedAt = now.AddDays(-90) });
            db.PlayerClubs.AddRange(
                new PlayerClub { FideId = "1", Club = "SK Alt", FetchedAt = now.AddDays(-181) },
                new PlayerClub { FideId = "2", Club = "SK Frisch", FetchedAt = now.AddDays(-179) });
            await db.SaveChangesAsync();

            var (jobs, clubs) = await RetentionService.PurgeAsync(db, now);

            Assert.Equal((2, 1), (jobs, clubs));
            Assert.Equal(new[] { "3", "4" },
                await db.CrawlJobs.AsNoTracking().Select(j => j.ChessResultsId).OrderBy(x => x).ToListAsync());
            Assert.Equal("2", (await db.PlayerClubs.AsNoTracking().SingleAsync()).FideId);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
