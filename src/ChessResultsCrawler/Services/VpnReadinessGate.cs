using ChessResultsCrawler.Models;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Wartet beim Service-Start, bis der gluetun-VPN-Tunnel tatsächlich Traffic durchlässt,
/// BEVOR der erste Crawl gegen chess-results.com losläuft.
///
/// Hintergrund: Nach einem (Re-)Deploy kommt der Crawler-Container teils schneller hoch als
/// der WireGuard-Tunnel wieder verbunden ist. Der Crawler startet dann sofort Crawls, die
/// auf Verbindungsebene scheitern ("Resource temporarily unavailable (chess-results.com:443)",
/// Status=null) und als <see cref="CrawlJobStatus.Failed"/> enden. Das Gate schließt diese
/// Lücke: es pollt den gluetun-Control-Server (<c>/v1/publicip/ip</c>) bis eine Public-IP
/// auflösbar ist (= Tunnel oben) oder ein Timeout greift.
///
/// In Umgebungen OHNE VPN (lokales Dev, <c>Gluetun:WaitForReady=false</c>) ist das Gate ein
/// No-Op und blockiert nichts.
/// </summary>
public class VpnReadinessGate
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VpnReadinessGate> _logger;
    private readonly bool _enabled;
    private readonly string _apiUrl;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;

    // Memoisierung: das volle Warten passiert nur EINMAL pro Prozess (beim Start). Spätere
    // Aufrufe kehren sofort zurück — mid-life Tunnel-Aussetzer fängt der Crawl-Retry ab.
    private readonly SemaphoreSlim _once = new(1, 1);
    private bool _ready;

    public VpnReadinessGate(IHttpClientFactory httpClientFactory, IConfiguration configuration,
        ILogger<VpnReadinessGate> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _enabled = configuration.GetValue("Gluetun:WaitForReady", false);
        _apiUrl = configuration["Gluetun:ApiUrl"] ?? configuration["Gluetun__ApiUrl"] ?? "http://localhost:8000";
        _timeout = TimeSpan.FromSeconds(configuration.GetValue("Gluetun:ReadyTimeoutSeconds", 120));
        _pollInterval = TimeSpan.FromSeconds(configuration.GetValue("Gluetun:ReadyPollSeconds", 3));
    }

    /// <summary>
    /// Kehrt erst zurück, wenn der VPN-Tunnel bereit ist (Public-IP auflösbar), das Timeout
    /// erreicht ist, oder <paramref name="ct"/> abgebrochen wird. Bei deaktiviertem Gate oder
    /// nach erstmaligem Erreichen der Bereitschaft kehrt sie sofort zurück.
    /// </summary>
    public async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        if (!_enabled || _ready)
            return;

        await _once.WaitAsync(ct);
        try
        {
            if (_ready)
                return;

            var client = _httpClientFactory.CreateClient("Gluetun");
            var deadline = DateTime.UtcNow + _timeout;
            var attempt = 0;
            _logger.LogInformation("Warte auf VPN-Tunnel-Bereitschaft (max {Timeout}s) vor dem ersten Crawl...",
                _timeout.TotalSeconds);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    var json = await client.GetStringAsync($"{_apiUrl}/v1/publicip/ip", ct);
                    var ip = CrawlerService.ParsePublicIp(json);
                    if (!string.IsNullOrWhiteSpace(ip))
                    {
                        _ready = true;
                        _logger.LogInformation("VPN-Tunnel bereit nach {Attempts} Versuch(en) → {PublicIp}", attempt, ip);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "VPN-Bereitschafts-Probe {Attempt} fehlgeschlagen", attempt);
                }

                try
                {
                    await Task.Delay(_pollInterval, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }

            // Timeout: NICHT hart blockieren — Crawls trotzdem zulassen (der Crawl-Retry fängt
            // verbleibende Verbindungsfehler ab). Als Warning, damit log-watcher es sieht.
            _ready = true;
            _logger.LogWarning("VPN-Tunnel nach {Timeout}s nicht als bereit bestätigt — fahre dennoch fort.",
                _timeout.TotalSeconds);
        }
        finally
        {
            _once.Release();
        }
    }
}
