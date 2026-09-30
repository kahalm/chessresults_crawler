using System.Text;
using Serilog.Context;
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
    private readonly int _restartPauseMs;

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
        // Dieselbe Einstellung wie im CrawlerService — die Rotation ist von dort hierher gezogen.
        _restartPauseMs = Math.Max(0, configuration.GetValue("Crawler:VpnRestartPauseMs", 3000));
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
    // ----- Ausgang wechseln -------------------------------------------------
    // Von CrawlerService hierher gezogen (2026-09-09): die Rotation wird jetzt von ZWEI Seiten
    // gebraucht — periodisch alle N Crawl-Anfragen und, neu, wenn eine QUELLE ihren Host vom
    // aktuellen Ausgang nicht erreicht (RotateOnConnectFailureHandler). Eine zweite Fassung
    // derselben stop→pause→start-Choreografie waere die erste Stelle, an der beide auseinander
    // laufen — und ein halb ausgefuehrter Wechsel laesst den Tunnel dauerhaft gestoppt zurueck.

    /// <summary>Wartezeit zwischen stop und start; Timeout der Steuer-Aufrufe.</summary>
    private const int VpnControlTimeoutMs = 30000;

    /// <summary>Der gluetun-Steuer-Client — dieselbe Konfiguration wie im CrawlerService
    /// (<see cref="GluetunClientSetup"/>).</summary>
    private HttpClient Gluetun() => _httpClientFactory.CreateClient("Gluetun");

    // ----- Laufende Crawl-Anfragen ------------------------------------------
    // Der Crawl-Riegel gilt nur fuer Drosselung und Wechsel; die Anfrage selbst laeuft danach ohne
    // ihn. Ein Wechsel, den ein zweiter Aufrufer ausloest, stoppte den Tunnel deshalb unter einer
    // laufenden Anfrage (im Mittel 3 s). Jede Crawl-Anfrage meldet sich darum hier an — noch UNTER
    // dem Riegel, sonst bliebe zwischen Freigabe und Anmeldung ein Fenster — und der Wechsel wartet,
    // bis keine mehr unterwegs ist (wie DrainInFlightAsync in piratechess).

    /// <summary>Hoechstens so lange wartet ein Wechsel auf laufende Anfragen, dann wechselt er
    /// trotzdem. Unter 60 s, damit die am Riegel Wartenden (60 s) nicht mit auslaufen.</summary>
    internal const int DrainTimeoutMs = 25000;
    private const int DrainPollMs = 25;
    private int _inFlight;

    /// <summary>Crawl-Anfragen, die gerade unterwegs sind (Diagnose/Tests).</summary>
    internal int InFlightRequests => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Meldet eine Crawl-Anfrage als unterwegs; <c>Dispose</c> meldet sie wieder ab. Der Aufrufer
    /// haelt dabei den Crawl-Riegel (<see cref="CrawlerService.CrawlGate"/>).
    /// </summary>
    internal IDisposable BeginRequest()
    {
        Interlocked.Increment(ref _inFlight);
        return new InFlightLease(this);
    }

    private sealed class InFlightLease(VpnReadinessGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref gate._inFlight);
        }
    }

    /// <summary>Wartet, bis keine Crawl-Anfrage mehr unterwegs ist (hoechstens
    /// <see cref="DrainTimeoutMs"/>). Ohne Aufrufer-Token, aus demselben Grund wie die Rotation.</summary>
    private async Task DrainInFlightAsync()
    {
        var waited = 0;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (waited >= DrainTimeoutMs)
            {
                _logger.LogWarning(
                    "Ausgangswechsel: {InFlight} Crawl-Anfrage(n) nach {WaitedMs} ms noch unterwegs — wechsle trotzdem",
                    Volatile.Read(ref _inFlight), waited);
                return;
            }
            await Task.Delay(DrainPollMs);
            waited += DrainPollMs;
        }
    }

    /// <summary>
    /// Wechselt den Ausgang und HAELT dabei den Crawl-Riegel — der Weg fuer Aufrufer, die ihn
    /// nicht schon halten (die Quellen-Abrufe). Waehrend des Wechsels ist der Tunnel unten, es
    /// darf also keine Crawl-Anfrage unterwegs sein.
    /// </summary>
    public async Task<bool> RotateAsync(CancellationToken ct)
    {
        if (!await CrawlerService.CrawlGate.WaitAsync(TimeSpan.FromSeconds(60), ct))
        {
            _logger.LogWarning("Ausgangswechsel uebersprungen: Crawl-Riegel nicht bekommen");
            return false;
        }
        try
        {
            await RotateWhileGateHeldAsync();
            return true;
        }
        finally
        {
            CrawlerService.CrawlGate.Release();
        }
    }
    /// <summary>
    /// Startet den VPN-Tunnel neu (stop→pause→start). Läuft UNTER dem Rate-Limiter-Lock, weil der
    /// Tunnel dabei kurz unten ist und in dieser Phase kein Crawl-Request rausgehen darf. Bewusst
    /// KURZ gehalten: die rein informative Public-IP-Ermittlung (bis zu 5 s Polling) wird detached
    /// außerhalb des Locks geloggt, damit wartende Crawls nicht zusätzlich blockiert werden.
    /// Nimmt bewusst KEIN Aufrufer-Token entgegen (siehe Kommentar im Rumpf).
    /// </summary>
    internal async Task RotateWhileGateHeldAsync()
    {
        // Neue Anfragen kommen nicht mehr durch (der Aufrufer haelt den Riegel) — die schon
        // laufenden erst zu Ende gehen lassen, dann den Tunnel stoppen.
        await DrainInFlightAsync();

        var statusUrl = $"{_apiUrl}/v1/vpn/status";
        // stop→pause→start ist eine ATOMARE Einheit: sobald das stop draußen ist, MUSS ein start
        // folgen. Deshalb läuft die Rotation bewusst NICHT mit dem Aufrufer-Token — bricht der
        // Request ab (RookHub-Proxy timeoutet nach 30 s, während der Rate-Limiter bis 60 s warten
        // lässt) oder trifft ein Shutdown/Deploy genau dieses Fenster, bliebe der gluetun-Tunnel
        // dauerhaft "stopped": jeder weitere Crawl scheitert dann auf Verbindungsebene, bis nach
        // 20 fehlgeschlagenen Requests zufällig die nächste Rotation ein start sendet.
        // Stattdessen ein eigener, kurzer Timeout-Token + garantierter Recovery-start.
        var stopSent = false;
        try
        {
            using var rotationCts = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(_restartPauseMs + VpnControlTimeoutMs));

            _logger.LogInformation("Rotating VPN IP...");
            // VOR dem Senden setzen: wirft das stop-PUT selbst (Timeout, Verbindungsabbruch beim
            // Antwort-Lesen), kann gluetun den Request trotzdem schon ausgeführt und den Tunnel
            // gestoppt haben. Erst nach der Bestätigung zu setzen hieße: genau im gefährlichsten
            // Fall läuft kein Recovery — der Tunnel bliebe dauerhaft "stopped". Ein überflüssiges
            // Recovery-„running" ist dagegen harmlos (idempotent).
            stopSent = true;
            await PutVpnStatusAsync(statusUrl, "stopped", rotationCts.Token);
            await Task.Delay(_restartPauseMs, rotationCts.Token);
            await PutVpnStatusAsync(statusUrl, "running", rotationCts.Token);
            stopSent = false;   // start bestätigt (2xx) → kein Recovery nötig
            // Nach der Rotation den Rate-Limiter-Zeitstempel zuruecksetzen, damit die
            // erste Anfrage ueber die neue Verbindung den vollen DelayMs-Abstand abwartet.
            CrawlerService.ResetThrottleClock();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VPN rotation failed (non-critical)");
            if (stopSent)
                await TryRecoverVpnStartAsync(statusUrl);
            return;
        }

        // Neue Public-IP NICHT mehr im Lock ermitteln (kostete bis zu 5 s Blockade aller Crawls).
        // Detached best-effort loggen, sobald gluetun die neue IP kennt.
        LogNewPublicIpDetached();
    }

    /// <summary>Baut den gluetun-Statusbody neu — ein HttpContent ist nur EINMAL sendbar.</summary>
    private static StringContent NewVpnStatusContent(string status) =>
        new($$"""{"status":"{{status}}"}""", Encoding.UTF8, "application/json");

    /// <summary>
    /// Setzt den Tunnel-Status und prueft die Antwort: ein Fehlerstatus (401 bei falschem
    /// <c>X-API-Key</c>, 5xx) wirft wie ein Transportfehler. Ohne die Pruefung galt ein
    /// abgelehnter Wechsel als gelungen, und nach einem abgelehnten start blieb das Recovery aus.
    /// </summary>
    private async Task PutVpnStatusAsync(string statusUrl, string status, CancellationToken ct)
    {
        using var response = await Gluetun().PutAsync(statusUrl, NewVpnStatusContent(status), ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Letzte Rettung nach einer abgebrochenen/fehlgeschlagenen Rotation: das start-PUT wird noch
    /// einmal gefeuert, damit kein gestoppter Tunnel zurückbleibt. Bewusst ohne jeden Aufrufer-
    /// Token und mit eigenem kurzen Timeout — genau der Abbruch war ja die Ursache.
    /// </summary>
    private async Task TryRecoverVpnStartAsync(string statusUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(VpnControlTimeoutMs));
            await PutVpnStatusAsync(statusUrl, "running", cts.Token);
            _logger.LogWarning("VPN rotation abgebrochen — Tunnel per Recovery-start reaktiviert");
        }
        catch (Exception ex)
        {
            // Hier ist der Tunnel womöglich wirklich unten → Error, damit der Alert greift.
            _logger.LogError(ex, "VPN recovery start failed — Tunnel bleibt moeglicherweise gestoppt");
        }
    }

    /// <summary>
    /// Ermittelt + loggt die neue Public-IP nach einer Rotation OHNE den Rate-Limiter zu halten
    /// (fire-and-forget, best-effort — rein zur Korrelation in ES/Kibana).
    /// </summary>
    private void LogNewPublicIpDetached()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var newIp = await TryGetPublicIpAsync(CancellationToken.None);
                using var _ = LogContext.PushProperty("LogTags", "crawl");
                if (newIp is not null)
                    _logger.LogInformation("VPN IP rotated → {NewIp}", newIp);
                else
                    _logger.LogInformation("VPN IP rotated (neue IP nicht ermittelbar)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "detached publicip logging failed");
            }
        });
    }

    /// <summary>
    /// Fragt die aktuelle Public-IP beim gluetun-Control-Server ab (best-effort, non-critical).
    /// gluetun braucht nach dem Reconnect kurz, bis die neue IP ermittelt ist → kurzes Polling.
    /// </summary>
    private async Task<string?> TryGetPublicIpAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await Task.Delay(1000, ct);
                var json = await Gluetun().GetStringAsync($"{_apiUrl}/v1/publicip/ip", ct);
                var ip = CrawlerService.ParsePublicIp(json);
                if (!string.IsNullOrWhiteSpace(ip)) return ip;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "publicip query attempt {Attempt} failed", attempt + 1);
            }
        }
        return null;
    }

}
