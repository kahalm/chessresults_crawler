namespace ChessResultsCrawler.Middleware;

/// <summary>
/// Uebersetzt Fehler, die beim Crawlen von chess-results.com (dem Upstream) entstehen, in
/// semantisch korrekte Gateway-Statuscodes statt nackter 500er:
///   - Upstream-Timeout (HttpClient 30s laeuft ab → TaskCanceledException, NICHT vom Client) → 504 Gateway Timeout
///   - Upstream nicht erreichbar / liefert non-2xx (EnsureSuccessStatusCode) → 502 Bad Gateway
///   - Client bricht die Verbindung ab (RequestAborted) → 499 Client Closed Request (kein Serverfehler)
/// So wertet der log-watcher externe chess-results.com-Haenger nicht mehr als echten Service-500.
/// Muss NACH UseSerilogRequestLogging registriert werden, damit das Request-Log den gemappten
/// Statuscode (504/502/499) sieht und nicht die rohe Exception als Error meldet.
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
            // Nicht vom Client ausgeloest → der ausgehende HttpClient-Timeout (30s) gegen
            // chess-results.com ist abgelaufen.
            _logger.LogWarning(ex, "Upstream request to chess-results.com timed out for {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status504GatewayTimeout,
                "Upstream request to chess-results.com timed out.");
        }
        catch (HttpRequestException ex)
        {
            // chess-results.com nicht erreichbar oder liefert non-2xx (EnsureSuccessStatusCode).
            _logger.LogWarning(ex, "Upstream request to chess-results.com failed for {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway,
                "Upstream request to chess-results.com failed.");
        }
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
