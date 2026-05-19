using System.Diagnostics;
using System.Security.Claims;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;

namespace ChessResultsCrawler.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;

    private const int MaxResponseBodyLength = 64 * 1024; // 64 KB
    private static readonly string[] ExcludedPrefixes = ["/health", "/swagger"];

    public RequestLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopeFactory)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // Replace response stream to capture the body
        var originalBodyStream = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await _next(context);

        stopwatch.Stop();

        // Read response body if JSON
        string? responseBody = null;
        var contentType = context.Response.ContentType;
        if (contentType != null && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            memoryStream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(memoryStream, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            if (body.Length > MaxResponseBodyLength)
                responseBody = body[..MaxResponseBodyLength];
            else
                responseBody = body;
        }

        // Copy response back to original stream
        memoryStream.Seek(0, SeekOrigin.Begin);
        await memoryStream.CopyToAsync(originalBodyStream);
        context.Response.Body = originalBodyStream;

        var log = new RequestLog
        {
            Timestamp = DateTime.UtcNow,
            Method = context.Request.Method,
            Path = path.Length > 500 ? path[..500] : path,
            QueryString = TruncateOrNull(context.Request.QueryString.ToString(), 1000),
            UserName = context.User?.Identity?.Name,
            UserId = ParseUserId(context.User),
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            StatusCode = context.Response.StatusCode,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ResponseBody = responseBody
        };

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.RequestLogs.Add(log);
                await db.SaveChangesAsync();
            }
            catch
            {
                // Logging failures must not affect request processing
            }
        });
    }

    private static int? ParseUserId(ClaimsPrincipal? user)
    {
        var claim = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
