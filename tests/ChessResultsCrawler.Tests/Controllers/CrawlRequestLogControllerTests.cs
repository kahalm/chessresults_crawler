using ChessResultsCrawler.Controllers;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Controllers;

public class CrawlRequestLogControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly CrawlRequestLogController _controller;

    public CrawlRequestLogControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _controller = new CrawlRequestLogController(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task SeedLogsAsync()
    {
        _db.CrawlRequestLogs.AddRange(
            new CrawlRequestLog
            {
                Timestamp = new DateTime(2026, 5, 1),
                Url = "https://chess-results.com/tnr123.aspx",
                StatusCode = 200,
                DurationMs = 150,
                ResponseSizeBytes = 5000,
                ResponseBody = "<html>page1</html>",
                Success = true
            },
            new CrawlRequestLog
            {
                Timestamp = new DateTime(2026, 5, 2),
                Url = "https://chess-results.com/tnr456.aspx",
                StatusCode = null,
                DurationMs = 3000,
                Success = false,
                ErrorMessage = "Connection refused",
                IsRetry = false
            },
            new CrawlRequestLog
            {
                Timestamp = new DateTime(2026, 5, 3),
                Url = "https://chess-results.com/tnr456.aspx",
                StatusCode = 200,
                DurationMs = 200,
                ResponseSizeBytes = 8000,
                ResponseBody = "<html>page2</html>",
                Success = true,
                IsRetry = true
            },
            new CrawlRequestLog
            {
                Timestamp = new DateTime(2026, 5, 4),
                Url = "https://chess-results.com/SpielerSuche.aspx",
                StatusCode = 500,
                DurationMs = 50,
                Success = false,
                ErrorMessage = "Internal Server Error"
            }
        );
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetLogs_ReturnsAllLogs()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, null, null, null) as OkObjectResult;

        Assert.NotNull(result);
        dynamic data = result.Value!;
        var totalCount = (int)data.GetType().GetProperty("totalCount")!.GetValue(data);
        Assert.Equal(4, totalCount);
    }

    [Fact]
    public async Task GetLogs_FilterByUrl()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, "tnr456", null, null) as OkObjectResult;

        Assert.NotNull(result);
        var totalCount = (int)result.Value!.GetType().GetProperty("totalCount")!.GetValue(result.Value);
        Assert.Equal(2, totalCount);
    }

    [Fact]
    public async Task GetLogs_FilterByStatusCode()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, null, 500, null) as OkObjectResult;

        Assert.NotNull(result);
        var totalCount = (int)result.Value!.GetType().GetProperty("totalCount")!.GetValue(result.Value);
        Assert.Equal(1, totalCount);
    }

    [Fact]
    public async Task GetLogs_FilterBySuccess()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, null, null, false) as OkObjectResult;

        Assert.NotNull(result);
        var totalCount = (int)result.Value!.GetType().GetProperty("totalCount")!.GetValue(result.Value);
        Assert.Equal(2, totalCount);
    }

    [Fact]
    public async Task GetLogs_FilterByDateRange()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(
            new DateTime(2026, 5, 2), new DateTime(2026, 5, 3),
            null, null, null) as OkObjectResult;

        Assert.NotNull(result);
        var totalCount = (int)result.Value!.GetType().GetProperty("totalCount")!.GetValue(result.Value);
        Assert.Equal(2, totalCount);
    }

    [Fact]
    public async Task GetLogs_Pagination()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, null, null, null, false, 1, 2) as OkObjectResult;

        Assert.NotNull(result);
        var items = (System.Collections.IList)result.Value!.GetType().GetProperty("items")!.GetValue(result.Value)!;
        Assert.Equal(2, items.Count);
        var totalCount = (int)result.Value!.GetType().GetProperty("totalCount")!.GetValue(result.Value);
        Assert.Equal(4, totalCount);
    }

    [Fact]
    public async Task GetLogs_PageSizeCapped()
    {
        await SeedLogsAsync();

        // pageSize=500 should be capped to 200
        var result = await _controller.GetLogs(null, null, null, null, null, false, 1, 500) as OkObjectResult;

        Assert.NotNull(result);
        var pageSize = (int)result.Value!.GetType().GetProperty("pageSize")!.GetValue(result.Value);
        Assert.Equal(200, pageSize);
    }

    [Fact]
    public async Task GetLogs_IncludeBodyFalse_OmitsResponseBody()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, "tnr123", null, null, includeBody: false) as OkObjectResult;

        Assert.NotNull(result);
        var items = (System.Collections.IList)result.Value!.GetType().GetProperty("items")!.GetValue(result.Value)!;
        Assert.Single(items);
        var item = items[0]!;
        var body = item.GetType().GetProperty("ResponseBody")!.GetValue(item);
        Assert.Null(body);
    }

    [Fact]
    public async Task GetLogs_IncludeBodyTrue_ReturnsResponseBody()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, "tnr123", null, null, includeBody: true) as OkObjectResult;

        Assert.NotNull(result);
        var items = (System.Collections.IList)result.Value!.GetType().GetProperty("items")!.GetValue(result.Value)!;
        Assert.Single(items);
        var item = items[0]!;
        var body = (string?)item.GetType().GetProperty("ResponseBody")!.GetValue(item);
        Assert.Equal("<html>page1</html>", body);
    }

    [Fact]
    public async Task GetLogs_OrderByTimestampDescending()
    {
        await SeedLogsAsync();

        var result = await _controller.GetLogs(null, null, null, null, null) as OkObjectResult;

        Assert.NotNull(result);
        var items = (System.Collections.IList)result.Value!.GetType().GetProperty("items")!.GetValue(result.Value)!;
        var first = items[0]!;
        var url = (string)first.GetType().GetProperty("Url")!.GetValue(first)!;
        Assert.Contains("SpielerSuche", url); // Latest (May 4)
    }
}
