using System.Security.Cryptography;
using System.Text;

namespace ChessResultsCrawler.Middleware;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";

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

        // Allow health and swagger endpoints without key (exakter/segment-genauer
        // Match, damit z.B. "/api/healthXYZ" NICHT als offen durchrutscht).
        var path = context.Request.Path.Value ?? "";
        if (IsOpenPath(path))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey) ||
            !KeysEqual(providedKey.ToString(), _apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Invalid or missing API key." });
            return;
        }

        await _next(context);
    }

    private static bool IsOpenPath(string path) =>
        path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/health/ip", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/swagger", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger/", StringComparison.OrdinalIgnoreCase);

    // SHA-256 bringt beide Werte auf gleiche Laenge, damit FixedTimeEquals nicht
    // ueber unterschiedliche Laengen die Key-Laenge ueber die Vergleichszeit leakt.
    private static bool KeysEqual(string provided, string expected)
    {
        var hp = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var he = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(hp, he);
    }
}
