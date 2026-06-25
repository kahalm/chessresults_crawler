using System.Security.Cryptography;
using System.Text;

namespace ChessResultsCrawler.Middleware;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly string? _apiKey;
    private readonly bool _isProduction;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config, IHostEnvironment env)
    {
        _next = next;
        _apiKey = config["API_KEY"];
        _isProduction = env.IsProduction();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Allow health and swagger endpoints without key (exakter/segment-genauer
        // Match, damit z.B. "/api/healthXYZ" NICHT als offen durchrutscht).
        var path = context.Request.Path.Value ?? "";
        if (IsOpenPath(path))
        {
            await _next(context);
            return;
        }

        // Kein Key konfiguriert: in Development absichtlich offen (lokaler Fallback), in
        // Production aber fail-CLOSED — eine Fehlkonfiguration darf das Gate nicht öffnen.
        if (string.IsNullOrEmpty(_apiKey))
        {
            if (_isProduction)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { message = "API key not configured." });
                return;
            }
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
        // Nur die reine Liveness-Probe ist offen. /api/health/ip NICHT — der Endpoint gibt die
        // VPN-Exit-IP preis und triggert einen Outbound-Call (ipify); jetzt API-Key-pflichtig.
        path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
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
