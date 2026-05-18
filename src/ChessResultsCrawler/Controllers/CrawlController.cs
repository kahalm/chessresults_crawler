using ChessResultsCrawler.Data;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CrawlController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CrawlerService _crawler;

    public CrawlController(AppDbContext db, CrawlerService crawler)
    {
        _db = db;
        _crawler = crawler;
    }

    /// <summary>
    /// Start a new crawl job.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> StartCrawl([FromBody] CrawlRequest request)
    {
        if (!Enum.TryParse<CrawlJobType>(request.JobType, true, out var jobType))
            return BadRequest(new { error = $"Invalid job type: {request.JobType}" });

        var job = new CrawlJob
        {
            ChessResultsId = request.ChessResultsId,
            JobType = jobType
        };

        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        // Fire and forget - run crawl in background
        _ = Task.Run(async () =>
        {
            using var scope = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var crawler = scope.ServiceProvider.GetRequiredService<CrawlerService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobToRun = await db.CrawlJobs.FindAsync(job.Id);
            if (jobToRun is not null)
                await crawler.ExecuteCrawlAsync(jobToRun);
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
