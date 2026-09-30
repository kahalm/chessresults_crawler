using Microsoft.Extensions.Options;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Einstellungen des gluetun-Control-Servers — an EINER Stelle gelesen (<see cref="From"/>) und
/// einmal als <see cref="IOptions{TOptions}"/> registriert (<see cref="GluetunClientSetup.AddGluetunControl"/>).
/// Frueher lasen CrawlerService und VpnReadinessGate dieselben Schluessel je fuer sich, mit
/// eigenen Vorgaben — wirksam war nur die Lesung im Gate.
///
/// Eine Umgebungsvariable <c>Gluetun__ApiUrl</c> kommt hier als <c>Gluetun:ApiUrl</c> an (das
/// macht der Umgebungsvariablen-Provider von .NET); ein Rueckfall auf den Schluessel
/// "Gluetun__ApiUrl" fand deshalb nie einen Wert. Die Bereitschafts-Einstellungen
/// (<c>Gluetun:WaitForReady</c> …) liest nur das Gate selbst.
/// </summary>
public sealed class GluetunOptions
{
    public const string DefaultApiUrl = "http://localhost:8000";
    public const int DefaultRestartPauseMs = 3000;

    /// <summary>Basis-URL des Control-Servers (<c>Gluetun:ApiUrl</c>).</summary>
    public string ApiUrl { get; init; } = DefaultApiUrl;

    /// <summary>Optionaler <c>X-API-Key</c> (<c>Gluetun:ApiKey</c>); leer → kein Header.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Pause zwischen stop und start eines Ausgangswechsels
    /// (<c>Crawler:VpnRestartPauseMs</c>, nie negativ).</summary>
    public int RestartPauseMs { get; init; } = DefaultRestartPauseMs;

    public static GluetunOptions From(IConfiguration configuration) => new()
    {
        ApiUrl = configuration["Gluetun:ApiUrl"] ?? DefaultApiUrl,
        ApiKey = configuration["Gluetun:ApiKey"],
        RestartPauseMs = Math.Max(0, configuration.GetValue("Crawler:VpnRestartPauseMs", DefaultRestartPauseMs)),
    };
}

/// <summary>
/// Zentrale Konfiguration des benannten "Gluetun"-HttpClients, über den ALLE Aufrufe an den
/// gluetun-Control-Server laufen (Readiness-Poll und Ausgangswechsel im <see cref="VpnReadinessGate"/>).
///
/// Optionaler API-Key: gluetun kann seinen Control-Server per Role-Auth mit einem
/// <c>X-API-Key</c>-Header absichern. Ist <c>Gluetun:ApiKey</c> gesetzt, wird der Header an
/// jeden Control-Server-Aufruf gehängt; leer/nicht gesetzt → exakt bisheriges Verhalten
/// (kein Header). Die Aktivierung in Prod (Key in gluetun UND hier setzen) ist Deploy-Sache.
/// </summary>
public static class GluetunClientSetup
{
    internal const string ApiKeyHeaderName = "X-API-Key";

    /// <summary>Registriert die <see cref="GluetunOptions"/> (einmal gelesen, beim ersten Bedarf)
    /// und den benannten "Gluetun"-Client.</summary>
    public static IServiceCollection AddGluetunControl(this IServiceCollection services)
    {
        services.AddSingleton<IOptions<GluetunOptions>>(sp =>
            Options.Create(GluetunOptions.From(sp.GetRequiredService<IConfiguration>())));
        services.AddHttpClient("Gluetun", (sp, client) =>
            Configure(client, sp.GetRequiredService<IOptions<GluetunOptions>>().Value));
        return services;
    }

    public static void Configure(HttpClient client, GluetunOptions options)
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            client.DefaultRequestHeaders.Add(ApiKeyHeaderName, options.ApiKey);
    }
}
