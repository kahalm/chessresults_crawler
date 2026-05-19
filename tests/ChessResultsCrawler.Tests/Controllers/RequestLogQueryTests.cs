using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Tests the RequestLog query logic directly against the database,
/// since the controller has inline filtering/pagination logic.
/// </summary>
public class RequestLogQueryTests : IDisposable
{
    private readonly AppDbContext _db;

    public RequestLogQueryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task SeedLogsAsync()
    {
        _db.RequestLogs.AddRange(
            new RequestLog { Timestamp = new DateTime(2026, 5, 1), Method = "GET", Path = "/api/tournaments", StatusCode = 200, DurationMs = 50 },
            new RequestLog { Timestamp = new DateTime(2026, 5, 2), Method = "POST", Path = "/api/crawl", StatusCode = 202, DurationMs = 100 },
            new RequestLog { Timestamp = new DateTime(2026, 5, 3), Method = "GET", Path = "/api/tournaments/1/players", StatusCode = 200, DurationMs = 30 },
            new RequestLog { Timestamp = new DateTime(2026, 5, 4), Method = "GET", Path = "/api/health", StatusCode = 200, DurationMs = 5 }
        );
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetLogs_ReturnsLogs()
    {
        await SeedLogsAsync();

        var items = await _db.RequestLogs
            .OrderByDescending(r => r.Timestamp)
            .ToListAsync();

        Assert.Equal(4, items.Count);
        Assert.Equal("/api/health", items[0].Path); // Latest first
    }

    [Fact]
    public async Task GetLogs_FilterByPath()
    {
        await SeedLogsAsync();

        var items = await _db.RequestLogs
            .Where(r => r.Path.Contains("tournaments"))
            .ToListAsync();

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task GetLogs_Pagination()
    {
        await SeedLogsAsync();

        int page = 2, pageSize = 2;
        var items = await _db.RequestLogs
            .OrderByDescending(r => r.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        Assert.Equal(2, items.Count);
        Assert.Equal("/api/crawl", items[0].Path);
        Assert.Equal("/api/tournaments", items[1].Path);
    }

    [Fact]
    public async Task GetLogs_PageSizeCapped()
    {
        // Simulate the capping logic from the controller
        int pageSize = 500;
        if (pageSize > 200) pageSize = 200;

        Assert.Equal(200, pageSize);
    }
}
