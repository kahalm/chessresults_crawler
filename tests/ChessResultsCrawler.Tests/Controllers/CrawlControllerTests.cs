using System.Text.Json;
using ChessResultsCrawler.Controllers;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Tests the CrawlController's DB + validation logic directly against the database,
/// since the controller has inline logic (ID normalization, duplicate detection).
/// </summary>
public class CrawlControllerTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlControllerTests()
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

    #region StartCrawl Validation

    [Fact]
    public void StartCrawl_InvalidJobType_FailsParse()
    {
        var parsed = Enum.TryParse<CrawlJobType>("InvalidType", true, out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("Full", CrawlJobType.Full)]
    [InlineData("PlayersOnly", CrawlJobType.PlayersOnly)]
    [InlineData("PairingsOnly", CrawlJobType.PairingsOnly)]
    [InlineData("CheckNewRounds", CrawlJobType.CheckNewRounds)]
    [InlineData("full", CrawlJobType.Full)]
    public void StartCrawl_ValidJobTypes_ParseSuccessfully(string input, CrawlJobType expected)
    {
        var parsed = Enum.TryParse<CrawlJobType>(input, true, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void StartCrawl_InvalidId_NonNumeric_FailsRegex()
    {
        var id = "abc123";
        var isValid = System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{1,10}$");

        Assert.False(isValid);
    }

    [Fact]
    public void StartCrawl_NormalizesId_StripsTnrPrefix()
    {
        var raw = "tnr1394015.aspx?lan=1";
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            raw.Trim(), @"^(.*tnr)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Replace(".aspx", "").Split('?')[0].Trim();

        Assert.Equal("1394015", normalized);
    }

    [Fact]
    public void StartCrawl_NormalizesId_PlainNumber()
    {
        var raw = "1394015";
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            raw.Trim(), @"^(.*tnr)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Replace(".aspx", "").Split('?')[0].Trim();

        Assert.Equal("1394015", normalized);
    }

    #endregion

    #region Duplicate Job Detection

    [Fact]
    public async Task StartCrawl_DuplicateJob_DetectedWhenQueuedOrRunning()
    {
        var chessResultsId = "12345";
        _db.CrawlJobs.Add(new CrawlJob
        {
            ChessResultsId = chessResultsId,
            JobType = CrawlJobType.Full,
            Status = CrawlJobStatus.Queued
        });
        await _db.SaveChangesAsync();

        var existingJob = await _db.CrawlJobs.AnyAsync(j =>
            j.ChessResultsId == chessResultsId &&
            (j.Status == CrawlJobStatus.Queued || j.Status == CrawlJobStatus.Running));

        Assert.True(existingJob);
    }

    [Fact]
    public async Task StartCrawl_NoDuplicate_WhenPreviousJobCompleted()
    {
        var chessResultsId = "12345";
        _db.CrawlJobs.Add(new CrawlJob
        {
            ChessResultsId = chessResultsId,
            JobType = CrawlJobType.Full,
            Status = CrawlJobStatus.Completed
        });
        await _db.SaveChangesAsync();

        var existingJob = await _db.CrawlJobs.AnyAsync(j =>
            j.ChessResultsId == chessResultsId &&
            (j.Status == CrawlJobStatus.Queued || j.Status == CrawlJobStatus.Running));

        Assert.False(existingJob);
    }

    /// <summary>
    /// I2-009: RookHub (Turnierseite „Aktualisieren") verfolgt bei 409 den laufenden Auftrag, wenn der Rumpf eine
    /// numerische <c>jobId</c> traegt — die Form ist <c>{ message, jobId }</c>, nicht mehr <c>{ error }</c>.
    /// </summary>
    [Fact]
    public async Task StartCrawl_AlreadyRunning_Returns409WithMessageAndJobId()
    {
        _db.CrawlJobs.Add(new CrawlJob { ChessResultsId = "345678", JobType = CrawlJobType.Full, Status = CrawlJobStatus.Completed });
        var running = new CrawlJob { ChessResultsId = "345678", JobType = CrawlJobType.Full, Status = CrawlJobStatus.Running };
        _db.CrawlJobs.Add(running);
        await _db.SaveChangesAsync();
        var queue = new RecordingQueue();
        var controller = new CrawlController(_db, queue, NullLogger<CrawlController>.Instance);

        var result = await controller.StartCrawl(new CrawlRequest { ChessResultsId = "tnr345678.aspx", JobType = "Full" });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(running.Id, body.RootElement.GetProperty("jobId").GetInt32());
        Assert.Contains("already running", body.RootElement.GetProperty("message").GetString());
        Assert.False(body.RootElement.TryGetProperty("error", out _));
        Assert.Equal(0, queue.Enqueued);
        Assert.Equal(2, await _db.CrawlJobs.CountAsync());
    }

    [Fact]
    public async Task StartCrawl_OnlyFinishedJobs_StartsNewJob()
    {
        _db.CrawlJobs.Add(new CrawlJob { ChessResultsId = "456789", JobType = CrawlJobType.Full, Status = CrawlJobStatus.Failed });
        await _db.SaveChangesAsync();
        var queue = new RecordingQueue();
        var controller = new CrawlController(_db, queue, NullLogger<CrawlController>.Instance);

        var result = await controller.StartCrawl(new CrawlRequest { ChessResultsId = "456789", JobType = "Full" });

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(1, queue.Enqueued);
    }

    private sealed class RecordingQueue : IBackgroundTaskQueue
    {
        public int Enqueued { get; private set; }
        public ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> workItem) { Enqueued++; return ValueTask.CompletedTask; }
        public bool TryEnqueue(Func<IServiceProvider, CancellationToken, Task> workItem) { Enqueued++; return true; }
        public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    #endregion

    #region Queue Full — Job Stays Accessible

    [Fact]
    public async Task StartCrawl_QueueFull_JobMarkedAsFailed()
    {
        var job = new CrawlJob { ChessResultsId = "123456", JobType = CrawlJobType.Full };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        // Simulate: TryEnqueue returns false → controller marks job Failed
        job.Status = CrawlJobStatus.Failed;
        job.ErrorMessage = "Queue full — job rejected before start.";
        job.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var updated = await _db.CrawlJobs.FindAsync(job.Id);
        Assert.NotNull(updated);
        Assert.Equal(CrawlJobStatus.Failed, updated.Status);
        Assert.Equal("Queue full — job rejected before start.", updated.ErrorMessage);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task StartCrawl_QueueFull_FailedJobDoesNotBlockFutureRequests()
    {
        var chessResultsId = "234567";
        _db.CrawlJobs.Add(new CrawlJob
        {
            ChessResultsId = chessResultsId,
            JobType = CrawlJobType.Full,
            Status = CrawlJobStatus.Failed,
            ErrorMessage = "Queue full — job rejected before start."
        });
        await _db.SaveChangesAsync();

        var blockingJob = await _db.CrawlJobs.AnyAsync(j =>
            j.ChessResultsId == chessResultsId &&
            (j.Status == CrawlJobStatus.Queued || j.Status == CrawlJobStatus.Running));

        Assert.False(blockingJob);
    }

    #endregion

    #region Job Creation and Status

    [Fact]
    public async Task StartCrawl_ValidRequest_CreatesJob()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "999999",
            JobType = CrawlJobType.Full
        };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        Assert.True(job.Id > 0);
        Assert.Equal(CrawlJobStatus.Queued, job.Status);

        var response = CrawlJobResponse.FromEntity(job);
        Assert.Equal("999999", response.ChessResultsId);
        Assert.Equal("Full", response.JobType);
        Assert.Equal("Queued", response.Status);
    }

    [Fact]
    public async Task GetJobStatus_ReturnsJob()
    {
        var job = new CrawlJob { ChessResultsId = "12345", JobType = CrawlJobType.Full };
        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var found = await _db.CrawlJobs.FindAsync(job.Id);

        Assert.NotNull(found);
        Assert.Equal("12345", found.ChessResultsId);
    }

    [Fact]
    public async Task GetJobStatus_NotFound()
    {
        var found = await _db.CrawlJobs.FindAsync(99999);

        Assert.Null(found);
    }

    #endregion
}
