namespace ChessResultsCrawler.Middleware;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private static readonly HashSet<string> OpenPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health",
        "/swagger"
    };

    private readonly RequestDelegate _next;
    private readonly string? _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _apiKey = config["API_KEY"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip if no API key configured (backwards compatible)
        if (string.IsNullOrEmpty(_apiKey))
        {
            await _next(context);
            return;
        }

        // Allow health and swagger endpoints without key
        var path = context.Request.Path.Value ?? "";
        if (OpenPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey) ||
            providedKey.ToString() != _apiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Invalid or missing API key." });
            return;
        }

        await _next(context);
    }
}
