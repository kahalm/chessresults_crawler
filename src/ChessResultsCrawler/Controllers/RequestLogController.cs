using ChessResultsCrawler.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/request-logs")]
public class RequestLogController : ControllerBase
{
    private readonly AppDbContext _db;

    public RequestLogController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? path,
        [FromQuery] string? method,
        [FromQuery] string? userName,
        [FromQuery] int? minStatus,
        [FromQuery] bool includeBody = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 200) pageSize = 200;

        var query = _db.RequestLogs.AsQueryable();

        if (from.HasValue) query = query.Where(r => r.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(r => r.Timestamp <= to.Value);
        if (!string.IsNullOrEmpty(path))
        {
            if (path.Length > 200) path = path[..200];
            query = query.Where(r => r.Path.Contains(path));
        }
        if (!string.IsNullOrEmpty(method)) query = query.Where(r => r.Method == method);
        if (!string.IsNullOrEmpty(userName))
        {
            if (userName.Length > 100) userName = userName[..100];
            query = query.Where(r => r.UserName != null && r.UserName.Contains(userName));
        }
        if (minStatus.HasValue) query = query.Where(r => r.StatusCode >= minStatus.Value);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(r => r.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id,
                r.Timestamp,
                r.Method,
                r.Path,
                r.QueryString,
                r.UserName,
                r.UserId,
                r.IpAddress,
                r.StatusCode,
                r.DurationMs,
                ResponseBody = includeBody ? r.ResponseBody : null
            })
            .ToListAsync();

        return Ok(new { items, totalCount, page, pageSize });
    }
}
