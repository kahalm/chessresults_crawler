using ChessResultsCrawler.Services;
using Serilog.Context;

namespace ChessResultsCrawler.Middleware;

/// <summary>
/// Uebersetzt Fehler, die beim Crawlen des Upstreams (chess-results.com, ueber den Gluetun-VPN)
/// entstehen, in semantisch korrekte Gateway-Statuscodes statt nackter 500er:
///   - Upstream-Timeout (HttpClient 30s laeuft ab → TaskCanceledException, NICHT vom Client) → 504 Gateway Timeout
///   - Upstream nicht erreichbar / liefert non-2xx (EnsureSuccessStatusCode) → 502 Bad Gateway
///   - Quelle antwortet, aber nicht mit JSON (SourceResponseException, typisch Sperrseite mit 200)
///     → 502 Bad Gateway mit Auszug (source, upstreamStatus, contentType, excerpt, message)
///   - Eigener Rate-Limiter gesaettigt (TimeoutException nach 60s Wartezeit) → 503 Service Unavailable
///   - Client bricht die Verbindung ab (RequestAborted) → 499 Client Closed Request (kein Serverfehler)
/// So wertet der log-watcher externe Upstream-Haenger / Selbst-Drosselung nicht mehr als echten Service-500.
/// Muss NACH UseSerilogRequestLogging registriert werden, damit das Request-Log den gemappten
/// Statuscode (504/503/502/499) sieht und nicht die rohe Exception als Error meldet.
/// Hinweis: faengt bewusst nur diese Upstream-/Gateway-Klassen ab — echte interne Fehler (z.B.
/// NullReference, ungueltiger Parser-Zustand) propagieren weiter zu Kestrel als 500.
/// </summary>
public class UpstreamErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UpstreamErrorMiddleware> _logger;

    public UpstreamErrorMiddleware(RequestDelegate next, ILogger<UpstreamErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Der Client (RookHub-Proxy bzw. dessen Aufrufer) hat die Verbindung abgebrochen,
            // bevor wir fertig waren — kein Serverfehler.
            _logger.LogInformation("Request {Path} aborted by client", context.Request.Path);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 499; // nginx-Konvention: Client Closed Request
        }
        catch (OperationCanceledException ex)
        {
            // Nicht vom Client ausgeloest → der ausgehende HttpClient-Timeout (30s) gegen den
            // Upstream ist abgelaufen.
            using (LogContext.PushProperty("LogTags", "upstream,crawl"))
                _logger.LogWarning(ex, "Upstream request timed out for {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status504GatewayTimeout,
                "Upstream request timed out.");
        }
        catch (SourceResponseException ex)
        {
            // Der Crawler funktioniert, die QUELLE hat etwas anderes als JSON geliefert (Sperr-/
            // Warteseite). 502 statt einer unbehandelten 500 — der Rumpf mit Auszug reist bis in
            // RookHubs Nachtlauf-Log mit.
            using (LogContext.PushProperty("LogTags", "upstream,crawl"))
                _logger.LogWarning(ex, "Upstream source returned no JSON for {Path}", context.Request.Path);
            await WriteSourceResponseErrorAsync(context, ex);
        }
        catch (HttpRequestException ex)
        {
            // Upstream nicht erreichbar oder liefert non-2xx (EnsureSuccessStatusCode).
            using (LogContext.PushProperty("LogTags", "upstream,crawl"))
                _logger.LogWarning(ex, "Upstream request failed for {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway,
                "Upstream request failed.");
        }
        catch (TimeoutException ex)
        {
            // Eigener Rate-Limiter (CrawlerService) konnte das Ticket nicht binnen 60s holen →
            // Selbst-Drosselung/Ueberlast, kein Crash. 503 + Retry-After-Hinweis ist semantisch korrekt.
            // Keine Upstream-Störung, aber Teil des Crawl-Pfads → nur "crawl".
            using (LogContext.PushProperty("LogTags", "crawl"))
                _logger.LogWarning(ex, "Crawler rate limiter saturated for {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Service temporarily unavailable (crawler busy), please retry.");
        }
    }

    /// <summary>Dieselbe 502-Antwort, die frueher nur der KNSB-Controller schrieb.</summary>
    private static async Task WriteSourceResponseErrorAsync(HttpContext context, SourceResponseException ex)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new
        {
            source = ex.Source,
            upstreamStatus = ex.StatusCode,
            contentType = ex.ContentType,
            excerpt = ex.Excerpt,
            message = ex.Message,
        });
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string message)
    {
        // Antwort lief schon → Statuscode nicht mehr aenderbar; durchreichen.
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { message });
    }
}
