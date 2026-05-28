using ChessResultsCrawler.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/crawl-request-logs")]
public class CrawlRequestLogController : ControllerBase
{
    private readonly AppDbContext _db;

    public CrawlRequestLogController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? url,
        [FromQuery] int? statusCode,
        [FromQuery] bool? success,
        [FromQuery] bool includeBody = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 200) pageSize = 200;

        var query = _db.CrawlRequestLogs.AsQueryable();

        if (from.HasValue) query = query.Where(r => r.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(r => r.Timestamp <= to.Value);
        if (!string.IsNullOrEmpty(url))
        {
            if (url.Length > 200) url = url[..200];
            query = query.Where(r => r.Url.Contains(url));
        }
        if (statusCode.HasValue) query = query.Where(r => r.StatusCode == statusCode.Value);
        if (success.HasValue) query = query.Where(r => r.Success == success.Value);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(r => r.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id,
                r.Timestamp,
                r.Url,
                r.StatusCode,
                r.DurationMs,
                r.ResponseSizeBytes,
                ResponseBody = includeBody ? r.ResponseBody : null,
                r.Success,
                r.ErrorMessage,
                r.IsRetry
            })
            .ToListAsync();

        return Ok(new { items, totalCount, page, pageSize });
    }
}
