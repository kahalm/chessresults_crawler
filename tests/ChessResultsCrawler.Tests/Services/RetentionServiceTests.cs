using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Aufbewahrungsfristen (S3-018): alte Crawl-Aufträge nach 30 Tagen, Vereins-Suchergebnisse ohne
/// Auffrischung nach 180 Tagen; Turniere, Spieler und aktive Aufträge bleiben.
/// </summary>
public class RetentionServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly AppDbContext _db;

    public RetentionServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options);
    }

    public void Dispose() => _db.Dispose();

    private static CrawlJob Job(string id, CrawlJobStatus status, int createdDaysAgo, int? completedDaysAgo) => new()
    {
        ChessResultsId = id,
        Status = status,
        CreatedAt = Now.AddDays(-createdDaysAgo),
        CompletedAt = completedDaysAgo is null ? null : Now.AddDays(-completedDaysAgo.Value),
    };

    [Fact]
    public async Task Purge_DeletesFinishedJobsOlderThan30Days_KeepsRecentAndActiveOnes()
    {
        _db.CrawlJobs.AddRange(
            Job("done-old", CrawlJobStatus.Completed, 40, 31),
            Job("failed-old", CrawlJobStatus.Failed, 40, 35),
            Job("done-recent", CrawlJobStatus.Completed, 40, 29),     // Ende zählt, nicht das Anlegen
            Job("failed-legacy", CrawlJobStatus.Failed, 45, null),   // ohne Ende: Anlegen zählt
            Job("failed-legacy-new", CrawlJobStatus.Failed, 10, null),
            Job("queued-old", CrawlJobStatus.Queued, 90, null),      // aktiv: nie löschen
            Job("running-old", CrawlJobStatus.Running, 90, null));
        await _db.SaveChangesAsync();

        var (jobs, clubs) = await RetentionService.PurgeAsync(_db, Now);

        Assert.Equal((3, 0), (jobs, clubs));
        var left = await _db.CrawlJobs.AsNoTracking().Select(j => j.ChessResultsId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { "done-recent", "failed-legacy-new", "queued-old", "running-old" }, left);
    }

    [Fact]
    public async Task Purge_DeletesPlayerClubsNotRefreshedFor180Days()
    {
        _db.PlayerClubs.AddRange(
            new PlayerClub { FideId = "1", Club = "SK Alt", FetchedAt = Now.AddDays(-181) },
            new PlayerClub { FideId = "2", Club = null, FetchedAt = Now.AddDays(-400) },   // „keiner gefunden" genauso
            new PlayerClub { FideId = "3", Club = "SK Frisch", FetchedAt = Now.AddDays(-179) });
        await _db.SaveChangesAsync();

        var (jobs, clubs) = await RetentionService.PurgeAsync(_db, Now);

        Assert.Equal((0, 2), (jobs, clubs));
        Assert.Equal("3", (await _db.PlayerClubs.AsNoTracking().SingleAsync()).FideId);
    }

    /// <summary>RookHub verweist per CrawlerTournamentId auf Turniere — sie und ihre Spieler bleiben.</summary>
    [Fact]
    public async Task Purge_LeavesTournamentsAndPlayers()
    {
        var t = new Tournament { ChessResultsId = "1457876", Name = "Altes Open", CreatedAt = Now.AddYears(-3) };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();
        _db.Players.Add(new Player { TournamentId = t.Id, Snr = 1, Name = "Martinovic, Sasa", FideId = "14509792" });
        var job = Job("1457876", CrawlJobStatus.Completed, 1000, 1000);
        job.TournamentId = t.Id;
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var (jobs, _) = await RetentionService.PurgeAsync(_db, Now);

        Assert.Equal(1, jobs);
        Assert.Equal(1, await _db.Tournaments.CountAsync());
        Assert.Equal(1, await _db.Players.CountAsync());
    }

    [Fact]
    public async Task RunOnce_PurgesThroughItsOwnScope()
    {
        _db.CrawlJobs.Add(Job("done-old", CrawlJobStatus.Completed, 400, 400));
        _db.PlayerClubs.Add(new PlayerClub { FideId = "1", FetchedAt = DateTime.UtcNow.AddDays(-400) });
        await _db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        using var provider = services.BuildServiceProvider();
        var svc = new RetentionService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RetentionService>.Instance);

        await svc.RunOnceAsync();

        Assert.Equal(0, await _db.CrawlJobs.CountAsync());
        Assert.Equal(0, await _db.PlayerClubs.CountAsync());
    }

    /// <summary>Ein Fehler (DB weg) darf den Host nicht beenden — der nächste Lauf versucht es wieder.</summary>
    [Fact]
    public async Task RunOnce_SwallowsFailures()
    {
        var services = new ServiceCollection();   // kein AppDbContext registriert → Auflösen wirft
        using var provider = services.BuildServiceProvider();
        var svc = new RetentionService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RetentionService>.Instance);

        await svc.RunOnceAsync();   // wirft nicht
    }

    /// <summary>Ohne Registrierung liefe der Löschlauf nie.</summary>
    [Fact]
    public void Program_RegistersTheRetentionService()
    {
        var program = File.ReadAllText(RepoRoot.File("src", "ChessResultsCrawler", "Program.cs"));
        Assert.Contains("AddHostedService<RetentionService>()", program);
    }
}
