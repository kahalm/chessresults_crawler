using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace ChessResultsCrawler.Services;

/// <summary>
/// HTTP gegen chess-results.com: der EINE Crawl-Riegel (Mindestabstand, VPN-Rotation alle N Abrufe,
/// Anmeldung beim Ausgangswechsel), Redirects je Hop geprueft (SSRF), Retry bei 429/5xx und
/// Verbindungsfehlern, begrenztes Body-Lesen, der ASP.NET-Postback und die CrawlRequest-Logzeile.
/// Zustandslos bis auf den statischen Riegel; gebaut von <see cref="CrawlerService"/>.
/// </summary>
internal sealed class ChessResultsTransport
{
    private readonly HttpClient _httpClient;
    private readonly VpnReadinessGate _vpnGate;
    private readonly ILogger<CrawlerService> _logger;
    private readonly int _retryDelayMs;
    private readonly long _maxResponseBytes;
    private readonly int _rotateAfterRequests;
    private readonly int _minDelayMs;
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);

    /// <summary>
    /// Derselbe Riegel, den <see cref="RateLimitAsync"/> haelt — sichtbar, weil der Wechsel des
    /// VPN-Ausgangs ihn ebenfalls braucht: waehrend stop→pause→start ist der Tunnel unten, und in
    /// diesem Fenster darf keine Crawl-Anfrage unterwegs sein. Die Rotation selbst liegt seit
    /// 2026-09-09 in <see cref="VpnReadinessGate"/>, weil sie auch von den Quellen-Abrufen
    /// gebraucht wird; der Riegel bleibt hier, weil hier die Anfragen entstehen.
    /// </summary>
    internal static SemaphoreSlim CrawlGate => _rateLimiter;

    /// <summary>Nach einem Ausgangswechsel: der erste Abruf ueber die neue Verbindung soll den
    /// vollen Mindestabstand abwarten. Gehoert zum Riegel und bleibt deshalb hier.</summary>
    internal static void ResetThrottleClock() => _lastRequest = DateTime.UtcNow;
    private static DateTime _lastRequest = DateTime.MinValue;
    private static int _requestCount;
    /// <summary>
    /// Mindestabstand zweier Crawl-Abrufe. `internal`, weil <see cref="CrawlHttpHandler"/> KUERZER
    /// leben muss als dieser Abstand — sonst ueberlebt eine Verbindung die Rotationspause. Ein
    /// Test nagelt die Beziehung fest.
    /// </summary>
    internal const int DefaultMinDelayMs = 1500;
    private const int DefaultRotateAfterRequests = 20;
    private const int DefaultRetryDelayMs = 5000;

    // Defensives Obergrenze für die Größe einer chess-results.com-Antwort. Listen werden mit
    // zeilen=99999 geholt; ein bösartiger/fehlerhafter Server könnte beliebig große Bodies liefern
    // und den Heap sprengen. Großzügig (32 MB), aber endlich — sauberer Abbruch statt OOM.
    private const long DefaultMaxResponseBytes = 32L * 1024 * 1024;
    // HTTP-Retry pro Fetch: bis zu 3 Versuche bei Verbindungsfehlern und 429/5xx-Antworten.
    // Wartezeit: Retry-After-Header (Sekunden ODER HTTP-Datum) sofern vorhanden, sonst
    // exponentiell RetryDelayMs * 3^(n-1) → mit Default 5000 ms also 5 s / 15 s (bei 3 Versuchen gibt es nur 2 Wartezeiten).
    private const int FetchMaxAttempts = 3;
    // Deckel für serverseitige Retry-After-Angaben — schützt vor absurden/bösartigen Werten
    // (z.B. "Retry-After: 86400"), die einen Crawl sonst stundenlang schlafen legen würden.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Loggt unter der Kategorie <see cref="CrawlerService"/> — der log-watcher und Kibana
    /// filtern auf deren Textbausteine (z.B. „CrawlRequest …").</summary>
    public ChessResultsTransport(HttpClient httpClient, ILogger<CrawlerService> logger,
        IConfiguration configuration, VpnReadinessGate vpnGate)
    {
        _httpClient = httpClient;
        _vpnGate = vpnGate;
        _logger = logger;
        _retryDelayMs = configuration.GetValue("Crawler:RetryDelayMs", DefaultRetryDelayMs);
        _maxResponseBytes = Math.Max(1024, configuration.GetValue("Crawler:MaxResponseBytes", DefaultMaxResponseBytes));
        _rotateAfterRequests = Math.Max(1, configuration.GetValue("Crawler:RotateAfterRequests", DefaultRotateAfterRequests));
        _minDelayMs = Math.Max(0, configuration.GetValue("Crawler:MinDelayMs", DefaultMinDelayMs));
    }

    /// <summary>
    /// Liest den Antwort-Body als UTF-8-String, bricht aber bei Überschreiten von
    /// <see cref="_maxResponseBytes"/> sauber mit <see cref="InvalidOperationException"/> ab —
    /// schützt vor unbegrenzten Bodies (Heap-/OOM-Risiko) statt blind zu puffern.
    /// </summary>
    private async Task<string> ReadBodyBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
        {
            if (buffer.Length + read > _maxResponseBytes)
                throw new InvalidOperationException(
                    $"Response body exceeds maximum allowed size of {_maxResponseBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public async Task<string> FetchPageAsync(string baseUrl, string queryParams, CancellationToken ct = default)
    {
        var separator = baseUrl.Contains('?') ? "&" : "?";
        var url = $"{baseUrl}{separator}{queryParams}";
        return await FetchHtmlAsync(url, ct);
    }

    // SSRF-Schutz: nach (automatisch gefolgten) Redirects muss der finale Host
    // weiterhin chess-results.com sein. Exakter Vergleich, damit z.B.
    // "evilchess-results.com" oder "chess-results.com.attacker.tld" abgewiesen wird.
    internal static void EnsureChessResultsHost(string resolvedUrl)
    {
        Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri);
        var host = uri?.Host ?? string.Empty;
        if (!(host.Equals("chess-results.com", StringComparison.OrdinalIgnoreCase)
              || host.EndsWith(".chess-results.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Redirect to unexpected domain: {resolvedUrl}");
    }

    /// <summary>Ein zulässiges Request-Ziel muss https UND ein chess-results.com-Host sein.</summary>
    private static void EnsureAllowedTarget(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Refusing non-https target: {url}");
        EnsureChessResultsHost(url.ToString());
    }

    private static bool IsRedirectStatus(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Obergrenze der manuell gefolgten Redirect-Hops (Schleifen-/Kettenschutz).</summary>
    private const int MaxRedirectHops = 10;

    /// <summary>
    /// Führt einen Request aus und folgt Redirects MANUELL — jeder Hop wird VOR dem Absenden gegen
    /// https + chess-results.com geprüft (<see cref="EnsureAllowedTarget"/>). Ersetzt das
    /// automatische Redirect-Folgen des HttpClient (in Program.cs via <c>AllowAutoRedirect=false</c>
    /// abgeschaltet), das eine Kette blind bis zu 50 Hops folgte und erst die finale URL prüfte —
    /// der Outbound-Request an einen internen Host feuert damit gar nicht erst. Deckt GET wie POST
    /// (inkl. der POST-Antwort der Spielersuche) ab. Der Body wird bewusst erst beim FINALEN
    /// (nicht-Redirect-)Response gelesen. <paramref name="contentFactory"/> baut den POST-Body je
    /// Hop neu (eine HttpRequestMessage/Content ist nur einmal sendbar).
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpMethod method, Uri url, Func<HttpContent>? contentFactory, CancellationToken ct)
    {
        EnsureAllowedTarget(url);
        var currentMethod = method;
        var currentUrl = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(currentMethod, currentUrl);
            if (currentMethod == HttpMethod.Post) request.Content = contentFactory?.Invoke();
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            var status = response.StatusCode;
            var location = response.Headers.Location;
            if (!IsRedirectStatus(status) || location == null)
                return response;   // finale (nicht-Redirect-)Antwort → Aufrufer liest den Body

            response.Dispose();     // Redirect-Antwort: Body verwerfen, nicht herunterladen
            if (hop >= MaxRedirectHops)
                throw new InvalidOperationException($"Too many redirects (>{MaxRedirectHops}) for {url}");

            // Nächsten Hop bestimmen (relative Location gegen die aktuelle URL auflösen) + PRÜFEN,
            // BEVOR der nächste Request rausgeht.
            var next = new Uri(currentUrl, location);
            EnsureAllowedTarget(next);
            // 301/302/303 → als GET fortsetzen (Browser-/ASP.NET-Verhalten); 307/308 erhalten Methode+Body.
            if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther)
                currentMethod = HttpMethod.Get;
            currentUrl = next;
        }
    }

    public async Task<(string Url, string Html)> FetchWithRedirectAsync(string url, CancellationToken ct = default)
        => await FetchWithRetriesAsync(url, ct);

    public async Task<string> FetchHtmlAsync(string url, CancellationToken ct = default)
    {
        var (_, body) = await FetchWithRetriesAsync(url, ct);
        return body;
    }

    /// <summary>429 (Drosselung) und 5xx (Serverfehler) sind transient — ein Retry kann klappen.
    /// Andere 4xx (404, 403 …) werden durch Wiederholen nicht besser → kein Retry.</summary>
    internal static bool IsRetryableStatus(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>
    /// Liest den Retry-After-Header (delta-seconds ODER HTTP-Datum, RFC 9110). null wenn nicht
    /// vorhanden/nicht parsebar → Aufrufer nutzt den exponentiellen Fallback. Datum in der
    /// Vergangenheit → 0; nach oben auf <see cref="MaxRetryAfter"/> gedeckelt.
    /// </summary>
    internal static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response, DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : null);
        if (delay is null)
            return null;
        if (delay < TimeSpan.Zero)
            return TimeSpan.Zero;
        return delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }

    /// <summary>Exponentieller Fallback-Backoff (1-basierter Versuch): RetryDelayMs * 3^(n-1),
    /// mit Default 5000 ms also 5 s / 15 s (bei 3 Versuchen gibt es nur 2 Wartezeiten).</summary>
    private TimeSpan FetchBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(_retryDelayMs * Math.Pow(3, attempt - 1));

    /// <summary>
    /// Gemeinsame Fetch-Schleife mit bis zu <see cref="FetchMaxAttempts"/> Versuchen. Wiederholt
    /// werden reine Verbindungsfehler (kein HTTP-Status, z.B. Tunnel kurz weg) sowie 429/5xx-
    /// Antworten; die Wartezeit kommt aus dem Retry-After-Header, sonst exponentiell aus
    /// <see cref="FetchBackoff"/>. Andere 4xx werfen sofort. Nach dem letzten Versuch propagiert
    /// die HttpRequestException wie bisher (Crawl-Job → Failed bzw. Re-Queue-Logik im Aufrufer).
    /// </summary>
    private async Task<(string Url, string Body)> FetchWithRetriesAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var isRetry = attempt > 1;
            var inFlight = await RateLimitAsync(ct);
            _logger.LogDebug(isRetry ? "Retrying {Url}" : "Fetching {Url}", url);
            TimeSpan? retryAfter = null;
            var sw = Stopwatch.StartNew();
            try
            {
                // Redirects werden manuell + je Hop geprüft gefolgt (SSRF-Schutz vor dem Request).
                // using: mit ResponseHeadersRead gehört die Verbindung der Response bis zum Dispose —
                // ohne das hängt sie bei jedem Abbruch (z.B. Bounded-Read-Overflow) bis zur GC fest.
                using var response = await SendFollowingRedirectsAsync(HttpMethod.Get, new Uri(url), null, ct);
                var body = await ReadBodyBoundedAsync(response, ct);
                sw.Stop();
                LogCrawlRequest(url, (int)response.StatusCode, sw.ElapsedMilliseconds, body, response.IsSuccessStatusCode, null, isRetry);
                if (response.IsSuccessStatusCode)
                    return (response.RequestMessage?.RequestUri?.ToString() ?? url, body);

                // Nicht-retryfähig (404 …) oder letzter Versuch → wie bisher werfen. Die
                // HttpRequestException trägt den StatusCode und wird unten NICHT gefangen.
                if (!IsRetryableStatus(response.StatusCode) || attempt >= FetchMaxAttempts)
                    response.EnsureSuccessStatusCode();

                retryAfter = GetRetryAfterDelay(response, DateTimeOffset.UtcNow);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                // Reiner Verbindungsfehler (DNS, Reset, Tunnel weg) — kein HTTP-Status erhalten.
                sw.Stop();
                LogCrawlRequest(url, null, sw.ElapsedMilliseconds, null, false, ex.Message, isRetry);
                if (attempt >= FetchMaxAttempts)
                    throw;
                _logger.LogWarning(ex, "Fetch failed for {Url} (Versuch {Attempt}/{Max})",
                    url, attempt, FetchMaxAttempts);
            }
            finally
            {
                // Anfrage zu Ende (gelesen oder gescheitert) — VOR der Wartezeit abmelden, sonst
                // wartete ein Ausgangswechsel die ganze Pause bis zum naechsten Versuch mit ab.
                inFlight.Dispose();
            }

            var delay = retryAfter ?? FetchBackoff(attempt);
            _logger.LogWarning("Fetch {Url}: naechster Versuch {Attempt}/{Max} in {DelaySeconds:0.#}s{Source}",
                url, attempt + 1, FetchMaxAttempts, delay.TotalSeconds,
                retryAfter is not null ? " (Retry-After)" : "");
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>
    /// Erkennt reine Verbindungs-/Transportfehler (kein HTTP-Status erhalten) — typisch wenn der
    /// VPN-Tunnel kurz weg ist: "Resource temporarily unavailable", Socket-Reset/Refused, Timeout.
    /// Solche Fehler sind transient und rechtfertigen einen erneuten Crawl-Anlauf. HTTP-Fehler MIT
    /// Status (404/500…) gelten NICHT als transient, damit echte Serverfehler nicht endlos laufen.
    /// </summary>
    public static bool IsTransientConnectionError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException hre:
                    if (hre.StatusCode is null) return true;
                    break;
                case System.Net.Sockets.SocketException:
                case TimeoutException:
                case TaskCanceledException:
                case OperationCanceledException:
                    return true;
            }

            var m = e.Message;
            if (!string.IsNullOrEmpty(m) &&
                (m.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
                 || m.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                 || m.Contains("connection reset", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>Antwort eines Postbacks: die Formularseite aus dem GET (daraus liest z.B. die
    /// Turnierinfo den Namen), der Body der POST-Antwort und deren Medientyp.</summary>
    internal sealed record PostBackResponse(string FormHtml, string Body, string? MediaType);

    /// <summary>
    /// Der ASP.NET-Postback von chess-results an EINER Stelle: GET der Formularseite (gedrosselt,
    /// mit Retry, Redirects je Hop geprueft), die versteckten Felder EINMAL parsen, dann POST auf
    /// DIESELBE aufgeloeste Node-URL (s1/s2/...) — sonst passt der ViewState nicht zur Node.
    /// Cookies sind nicht noetig, der ViewState traegt den Zustand.
    ///
    /// <para>Die Formularfelder baut der Aufrufer aus <see cref="AspNetFormState"/>: welche
    /// Steuerfelder (__EVENTTARGET, __LASTFOCUS …) eine Seite bekommt, ist je Seite gewachsen und
    /// bleibt Sache der Seite; die Reihenfolge im Dictionary ist die Sendereihenfolge.</para>
    ///
    /// <para>Der POST geht genau einmal durch den Riegel und wird nicht wiederholt. Redirects auf
    /// die POST-Antwort werden ebenfalls manuell und je Hop geprueft gefolgt (schliesst die
    /// non-blind-SSRF-Luecke, bei der eine 3xx-POST-Antwort blind auf einen fremden Host fuehrte).
    /// Erst den Body lesen, dann EnsureSuccess: gesendet wird mit ResponseHeadersRead, die
    /// Verbindung geht erst beim Lesen/Disposen an den Pool zurueck — warf EnsureSuccess zuerst
    /// (500/429-Phasen von chess-results), blieb sie bis zur GC-Finalisierung belegt.</para>
    /// </summary>
    internal async Task<PostBackResponse> PostBackAsync(
        string formUrl, Func<AspNetFormState, Dictionary<string, string>> buildForm, CancellationToken ct)
    {
        var (resolvedUrl, formHtml) = await FetchWithRedirectAsync(formUrl, ct);
        // SSRF-Schutz: der POST geht nur an einen chess-results.com-Host.
        EnsureChessResultsHost(resolvedUrl);

        var formData = buildForm(AspNetForm.ExtractFormState(formHtml));

        using var inFlight = await RateLimitAsync(ct);
        using var response = await SendFollowingRedirectsAsync(
            HttpMethod.Post, new Uri(resolvedUrl), () => new FormUrlEncodedContent(formData), ct);

        var body = await ReadBodyBoundedAsync(response, ct);
        response.EnsureSuccessStatusCode();
        return new PostBackResponse(formHtml, body, response.Content.Headers.ContentType?.MediaType);
    }

    private void LogCrawlRequest(string url, int? statusCode, long durationMs, string? responseBody, bool success, string? error, bool isRetry)
    {
        // Bewusst KEIN Response-Body mehr ins Log: das gecrawlte HTML (bis 500 KB/Request) blähte
        // den Elasticsearch-Data-Stream massiv auf und enthielt ungefilterte Spieler-PII. Nur noch
        // die Größe (CrawlResponseSize) festhalten — der Inhalt ist ohnehin in der DB.
        // "crawl"-Tag auch hier, damit jeder einzelne Fetch-gegen-chess-results.com (unabhängig vom
        // Aufrufer, z.B. Spielersuche) zentral filterbar ist.
        using var _ = LogContext.PushProperty("LogTags", "crawl");
        _logger.LogInformation(
            "CrawlRequest {CrawlUrl} Status={CrawlStatusCode} Duration={CrawlDurationMs}ms " +
            "Size={CrawlResponseSize} Success={CrawlSuccess} IsRetry={CrawlIsRetry} Error={CrawlError}",
            url.Length > 2000 ? url[..2000] : url,
            statusCode, durationMs, responseBody?.Length,
            success, isRetry, error);
    }

    /// <summary>
    /// Genau EIN Durchlauf je HTTP-Anfrage: GETs gehen in <see cref="FetchWithRetriesAsync"/>
    /// durch den Riegel (je Versuch), der POST in <see cref="PostBackAsync"/> ruft ihn direkt davor.
    /// Ein zusaetzlicher Aufruf vor <see cref="FetchPageAsync"/> und Co. zaehlt doppelt: +1,5 s
    /// Wartezeit und die Rotation „alle N Abrufe" laeuft schon nach N/2.
    /// <para>Gibt die Anmeldung der Anfrage beim Ausgangswechsel zurueck: der Aufrufer gibt sie
    /// frei, sobald seine Anfrage fertig ist (Antwort gelesen oder gescheitert). Bis dahin wartet
    /// ein Wechsel (<see cref="VpnReadinessGate.RotateWhileGateHeldAsync"/>), statt den Tunnel
    /// unter der Anfrage zu stoppen.</para>
    /// </summary>
    private async Task<IDisposable> RateLimitAsync(CancellationToken ct = default)
    {
        if (!await _rateLimiter.WaitAsync(TimeSpan.FromSeconds(60), ct))
            throw new TimeoutException("Rate limiter acquisition timed out after 60 seconds.");
        try
        {
            // Rotate VPN IP every N requests. Der Semaphor wird NUR während des eigentlichen
            // Tunnel-Neustarts (stop→pause→start) gehalten — in dieser kurzen Phase ist der Tunnel
            // unten, also DARF ohnehin kein Crawl-Request raus. Die anschließende, rein informative
            // Public-IP-Ermittlung (bis zu 5×1 s Polling, nur fürs Logging) lief früher ebenfalls
            // im Lock und blockierte alle wartenden Crawls ~5 s zusätzlich (Timeout-Risiko) →
            // läuft jetzt detached außerhalb des Locks (siehe VpnReadinessGate.RotateWhileGateHeldAsync).
            _requestCount++;
            if (_requestCount >= _rotateAfterRequests)
            {
                _requestCount = 0;
                // Bewusst ohne ct: die Rotation muss auch bei Request-Abbruch zu Ende laufen,
                // sonst bleibt der Tunnel gestoppt (siehe VpnReadinessGate.RotateWhileGateHeldAsync).
                await _vpnGate.RotateWhileGateHeldAsync();
            }

            var elapsed = (DateTime.UtcNow - _lastRequest).TotalMilliseconds;
            if (elapsed < _minDelayMs)
            {
                await Task.Delay(_minDelayMs - (int)elapsed, ct);
            }
            _lastRequest = DateTime.UtcNow;
            // Noch UNTER dem Riegel anmelden: danach koennte sonst ein anderer Aufrufer den Riegel
            // nehmen und den Tunnel stoppen, bevor diese Anfrage als laufend zaehlt.
            return _vpnGate.BeginRequest();
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
