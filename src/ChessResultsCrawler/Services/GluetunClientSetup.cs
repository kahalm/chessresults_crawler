namespace ChessResultsCrawler.Services;

/// <summary>
/// Zentrale Konfiguration des benannten "Gluetun"-HttpClients, über den ALLE Aufrufe an den
/// gluetun-Control-Server laufen (VPN-Rotation im <see cref="CrawlerService"/>, Readiness-Poll
/// im <see cref="VpnReadinessGate"/>).
///
/// Optionaler API-Key: gluetun kann seinen Control-Server per Role-Auth mit einem
/// <c>X-API-Key</c>-Header absichern. Ist <c>Gluetun:ApiKey</c> gesetzt, wird der Header an
/// jeden Control-Server-Aufruf gehängt; leer/nicht gesetzt → exakt bisheriges Verhalten
/// (kein Header). Die Aktivierung in Prod (Key in gluetun UND hier setzen) ist Deploy-Sache.
/// </summary>
public static class GluetunClientSetup
{
    internal const string ApiKeyHeaderName = "X-API-Key";

    public static void Configure(HttpClient client, IConfiguration configuration)
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        // Wie bei Gluetun:ApiUrl auch die Env-Schreibweise mit "__" akzeptieren.
        var apiKey = configuration["Gluetun:ApiKey"] ?? configuration["Gluetun__ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Add(ApiKeyHeaderName, apiKey);
    }
}
