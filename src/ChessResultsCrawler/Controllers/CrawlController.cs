using ChessResultsCrawler.Data;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CrawlController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CrawlController> _logger;

    public CrawlController(AppDbContext db, IServiceScopeFactory scopeFactory, ILogger<CrawlController> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Start a new crawl job.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> StartCrawl([FromBody] CrawlRequest request)
    {
        if (!Enum.TryParse<CrawlJobType>(request.JobType, true, out var jobType))
            return BadRequest(new { error = $"Invalid job type: {request.JobType}" });

        // Normalize: strip "tnr" prefix and any URL parts so both "1394015" and "tnr1394015" work
        var chessResultsId = Regex.Replace(request.ChessResultsId.Trim(), @"^(.*tnr)", "", RegexOptions.IgnoreCase)
            .Replace(".aspx", "").Split('?')[0].Trim();

        // S-12: Whitelist – only numeric IDs allowed (prevents SSRF via manipulated IDs)
        if (!Regex.IsMatch(chessResultsId, @"^\d{1,10}$"))
            return BadRequest(new { error = "Invalid ChessResultsId. Only numeric IDs (1-10 digits) are allowed." });

        // M-13: Prevent duplicate crawl jobs for the same tournament
        var existingJob = await _db.CrawlJobs.AnyAsync(j =>
            j.ChessResultsId == chessResultsId &&
            (j.Status == CrawlJobStatus.Queued || j.Status == CrawlJobStatus.Running));
        if (existingJob)
            return Conflict(new { error = $"A crawl job for '{chessResultsId}' is already running." });

        var job = new CrawlJob
        {
            ChessResultsId = chessResultsId,
            JobType = jobType
        };

        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        // C-5/C-6: Capture scope factory before Task.Run to avoid accessing disposed HttpContext
        var scopeFactory = _scopeFactory;
        var jobId = job.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var crawler = scope.ServiceProvider.GetRequiredService<CrawlerService>();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var jobToRun = await db.CrawlJobs.FindAsync(jobId);
                if (jobToRun is not null)
                    await crawler.ExecuteCrawlAsync(jobToRun);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background crawl job {JobId} failed unexpectedly", jobId);
            }
        });

        return Accepted(CrawlJobResponse.FromEntity(job));
    }

    /// <summary>
    /// Get the status of a crawl job.
    /// </summary>
    [HttpGet("{jobId:int}")]
    public async Task<IActionResult> GetJobStatus(int jobId)
    {
        var job = await _db.CrawlJobs.FindAsync(jobId);
        if (job is null) return NotFound();
        return Ok(CrawlJobResponse.FromEntity(job));
    }
}
