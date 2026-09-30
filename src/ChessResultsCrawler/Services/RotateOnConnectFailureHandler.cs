using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Kommt zu einem Host KEINE Verbindung zustande, wird der VPN-Ausgang gewechselt und der Abruf
/// wiederholt — bis zu <see cref="MaxAttempts"/> Mal.
///
/// <para><b>Warum das noetig ist.</b> Mehrere Verbandsseiten sperren ganze Hosting-Netze, und zwar
/// verschiedene. Am 2026-09-09 auf Dev gemessen: vom deutschen AirVPN-Ausgang (alle auf M247) kam
/// zu <c>federscacchi.com</c> (Italien) und <c>frsah.ro</c> (Rumaenien) keine TCP-Verbindung
/// zustande, vom niederlaendischen (Global Layer) dagegen zu <c>chess.sk</c> (Slowakei) nicht.
/// Es gibt also KEINEN Ausgang, der alle Quellen erreicht — ein fester Standort tauscht nur ein
/// Land gegen ein anderes. Ein Wechsel mit Wiederholung macht die Wahl des Standorts gleichgueltig.
/// (Dieselbe Klasse Problem wie die Chessable-Cloudflare-Sperre, nur mit anderen Sperrlisten.)</para>
///
/// <para><b>Was NICHT wiederholt wird.</b> Nur das Nicht-Zustandekommen der Verbindung
/// (<see cref="HttpRequestException"/> ohne Statuscode, und der Abbruch durch
/// <c>SocketsHttpHandler.ConnectTimeout</c>). Eine ANTWORT der Quelle — auch 403, 429 oder 500 —
/// ist eine Auskunft und wird durchgereicht: einen Ausgang zu wechseln, weil die Seite gerade
/// einen Fehler meldet, verbrennt nur Zeit und Rotationen. Ebenso bleibt ein Abbruch durch den
/// Aufrufer ein Abbruch.</para>
///
/// <para><b>Der Wechsel haelt den Crawl-Riegel</b> (siehe <see cref="VpnReadinessGate.RotateAsync"/>):
/// waehrend stop→pause→start ist der Tunnel unten, in diesem Fenster darf keine
/// chess-results-Anfrage unterwegs sein.</para>
/// </summary>
public class RotateOnConnectFailureHandler : DelegatingHandler
{
    /// <summary>Versuche INSGESAMT, also bis zu vier Ausgangswechsel. Mehr bringt wenig: die
    /// Ausgaenge eines Landes liegen meist im selben Netz, und jeder Wechsel kostet die
    /// stop→start-Pause plus den Mindestabstand des Riegels.</summary>
    internal const int MaxAttempts = 5;

    private readonly VpnReadinessGate _vpnGate;
    private readonly ILogger<RotateOnConnectFailureHandler> _logger;
    private readonly TimeSpan _attemptTimeout;

    /// <param name="attemptTimeout">Zeitlimit JE VERSUCH. Es steht hier und nicht mehr als
    /// <c>HttpClient.Timeout</c>, weil das fuer die GANZE Sendung gilt — also fuer alle Versuche
    /// zusammen. Mit dem 30-s-Limit der Quelle war nach dem ersten Fehlversuch Schluss, und die
    /// Wiederholung kam nie zum Zug (am 2026-09-09 an der Slowakei gemessen: 500 nach genau 30 s).
    /// Die Schranke je Versuch liegt deshalb hier; das Client-Zeitlimit deckt alle Versuche samt
    /// Wechseln ab (<see cref="SourceClientSetup.ClientTimeout"/>).</param>
    public RotateOnConnectFailureHandler(VpnReadinessGate vpnGate,
        ILogger<RotateOnConnectFailureHandler> logger, TimeSpan attemptTimeout)
    {
        _vpnGate = vpnGate;
        _logger = logger;
        _attemptTimeout = attemptTimeout;
    }

    /// <summary>Das Zeitlimit je Versuch (fuer den Registrierungstest der Quellen-Clients).</summary>
    internal TimeSpan AttemptTimeout => _attemptTimeout;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "?";
        for (var attempt = 1; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(_attemptTimeout);
            try
            {
                return await base.SendAsync(request, attemptCts.Token);
            }
            catch (Exception ex) when (IsConnectFailure(ex, ct) && attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "{Host}: keine Verbindung vom aktuellen Ausgang (Versuch {Attempt}/{Max}) — wechsle",
                    host, attempt, MaxAttempts);
                await _vpnGate.RotateAsync(ct);
            }
        }
    }

    /// <summary>
    /// Kein Statuscode erhalten? Dann kam die Verbindung nicht zustande. Ein TIMEOUT des
    /// Verbindungsaufbaus erscheint als <see cref="TaskCanceledException"/>, OHNE dass der
    /// Aufrufer abgebrochen haette — genau diese Unterscheidung ist hier der Kern, sonst wuerde
    /// ein Abbruch durch den Aufrufer vier Ausgangswechsel ausloesen.
    /// </summary>
    internal static bool IsConnectFailure(Exception ex, CancellationToken ct) => ex switch
    {
        _ when ct.IsCancellationRequested => false,
        HttpRequestException => true,
        TaskCanceledException or OperationCanceledException => true,
        _ => false,
    };
}
