using System.Security.Cryptography;
using System.Text;

namespace ChessResultsCrawler.Middleware;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";

    /// <summary>
    /// Expliziter Schalter fuer den offenen lokalen Betrieb OHNE Schluessel (<c>API_KEY_ALLOW_ANONYMOUS=true</c>).
    /// Ohne ihn ist die API ohne (echten) <c>API_KEY</c> in JEDER Umgebung zu (503) — ein Staging-/Dev-Stack mit
    /// vergessenem Key steht nicht mehr still offen (gleiche Semantik wie das ServiceKeyAuth-Attribut in
    /// piratechess). In Production gilt der Schalter nicht; ein gesetzter Key wird immer verlangt.
    /// </summary>
    internal const string AllowAnonymousSetting = "API_KEY_ALLOW_ANONYMOUS";

    /// <summary>
    /// Anfaenge der Platzhalter in den .env-Vorlagen des Stacks (RookHub: <c>CRAWLER_API_KEY=change_me…</c>,
    /// dazu <c>your_…</c>) — dieselben wie im schach-bot. Die Vorlagen liegen in oeffentlichen Repos.
    /// </summary>
    private static readonly string[] PlaceholderPrefixes = ["change_me", "your_"];

    private readonly RequestDelegate _next;
    private readonly string? _apiKey;
    private readonly bool _allowAnonymous;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config, IHostEnvironment env,
        ILogger<ApiKeyMiddleware>? logger = null)
    {
        _next = next;
        var configured = config["API_KEY"];

        // Ein stehengebliebener Platzhalter ist ein oeffentlich bekannter Schluessel, also keiner:
        // er zaehlt wie „nicht gesetzt" (503; offen nur mit dem Schalter ausserhalb von Production).
        var placeholder = IsPlaceholder(configured);
        if (placeholder)
            logger?.LogError(
                "API_KEY ist ein Platzhalter aus einer .env-Vorlage — wie nicht gesetzt behandelt, die API antwortet mit 503. Echten, mit RookHub (CRAWLER_API_KEY) geteilten Wert setzen.");
        _apiKey = placeholder || string.IsNullOrWhiteSpace(configured) ? null : configured;

        _allowAnonymous = _apiKey is null && !env.IsProduction()
            && bool.TryParse(config[AllowAnonymousSetting]?.Trim(), out var allow) && allow;

        if (_allowAnonymous)
            logger?.LogWarning(
                "Kein API_KEY gesetzt und API_KEY_ALLOW_ANONYMOUS=true — die API ist OHNE Schluessel offen (nur fuer lokale Entwicklung).");
        else if (_apiKey is null && !placeholder)
            logger?.LogError(
                "API_KEY ist nicht gesetzt — die API antwortet (ausser /api/health) mit 503. Den mit RookHub (CRAWLER_API_KEY) geteilten Wert setzen; nur lokal ausserhalb von Production: API_KEY_ALLOW_ANONYMOUS=true.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Nur die Liveness-Probe ist ohne Key erreichbar (exakter Match, damit z.B.
        // "/api/healthXYZ" NICHT als offen durchrutscht).
        var path = context.Request.Path.Value ?? "";
        if (IsOpenPath(path))
        {
            await _next(context);
            return;
        }

        // Kein Key konfiguriert (oder nur der Platzhalter): fail-CLOSED in jeder Umgebung — eine
        // Fehlkonfiguration darf das Gate nicht oeffnen. Offen nur mit dem expliziten Schalter.
        if (_apiKey is null)
        {
            if (_allowAnonymous)
            {
                await _next(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { message = "API key not configured." });
            return;
        }

        // Genau EIN Header-Wert (wie piratechess): mehrere Werte werden abgewiesen, nicht zusammengefuegt.
        var providedKey = context.Request.Headers[ApiKeyHeader];
        if (providedKey.Count != 1 || !KeysEqual(providedKey.ToString(), _apiKey))
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
        // Swagger ist hier NICHT ausgenommen: die UI gibt es nur in Development, und dort liefert
        // UseSwagger/UseSwaggerUI sie vor dieser Middleware aus (Program.cs).
        path.Equals("/api/health", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPlaceholder(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return PlaceholderPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    // SHA-256 bringt beide Werte auf gleiche Laenge, damit FixedTimeEquals nicht
    // ueber unterschiedliche Laengen die Key-Laenge ueber die Vergleichszeit leakt.
    private static bool KeysEqual(string provided, string expected)
    {
        var hp = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var he = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(hp, he);
    }
}
