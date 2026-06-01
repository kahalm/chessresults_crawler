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
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly ILogger<CrawlController> _logger;

    public CrawlController(AppDbContext db, IBackgroundTaskQueue taskQueue, ILogger<CrawlController> logger)
    {
        _db = db;
        _taskQueue = taskQueue;
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
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Race: zwischen AnyAsync-Check und Insert hat ein paralleler Request bereits
            // einen aktiven Job angelegt (Unique-Index auf der ActiveKey-Computed-Column).
            return Conflict(new { error = $"A crawl job for '{chessResultsId}' is already running." });
        }

        var jobId = job.Id;
        if (!_taskQueue.TryEnqueue(async (sp, ct) =>
        {
            var crawler = sp.GetRequiredService<CrawlerService>();
            var db = sp.GetRequiredService<AppDbContext>();
            var jobToRun = await db.CrawlJobs.FindAsync(jobId);
            if (jobToRun is not null)
                await crawler.ExecuteCrawlAsync(jobToRun, ct);
        }))
        {
            return StatusCode(429, new { error = "Crawl queue is full. Try again later." });
        }

        return Accepted(CrawlJobResponse.FromEntity(job));
    }

    /// <summary>
    /// Crawl player detail pages (art=9) for specific player SNRs.
    /// </summary>
    [HttpPost("player-details")]
    public async Task<IActionResult> CrawlPlayerDetails([FromBody] PlayerDetailCrawlRequest request)
    {
        // Normalize ChessResultsId
        var chessResultsId = Regex.Replace(request.ChessResultsId.Trim(), @"^(.*tnr)", "", RegexOptions.IgnoreCase)
            .Replace(".aspx", "").Split('?')[0].Trim();

        if (!Regex.IsMatch(chessResultsId, @"^\d{1,10}$"))
            return BadRequest(new { error = "Invalid ChessResultsId. Only numeric IDs (1-10 digits) are allowed." });

        if (request.PlayerSnrs is null || request.PlayerSnrs.Count == 0)
            return BadRequest(new { error = "PlayerSnrs must contain at least one SNR." });

        if (request.PlayerSnrs.Count > 50)
            return BadRequest(new { error = "Maximum 50 player SNRs per request." });

        // Verify tournament exists
        var tournament = await _db.Tournaments
            .FirstOrDefaultAsync(t => t.ChessResultsId == chessResultsId);
        if (tournament is null)
            return NotFound(new { error = $"Tournament '{chessResultsId}' not found. Crawl the tournament first." });

        var snrs = request.PlayerSnrs.Distinct().ToList();
        if (!_taskQueue.TryEnqueue(async (sp, ct) =>
        {
            var crawler = sp.GetRequiredService<CrawlerService>();
            await crawler.CrawlPlayerDetailsAsync(chessResultsId, snrs, ct);
        }))
        {
            return StatusCode(429, new { error = "Crawl queue is full. Try again later." });
        }

        return Accepted(new { message = $"Player detail crawl started for {snrs.Count} player(s).", chessResultsId, playerSnrs = snrs });
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
