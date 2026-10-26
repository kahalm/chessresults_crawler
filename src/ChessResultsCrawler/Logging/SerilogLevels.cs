using Serilog;
using Serilog.Events;

namespace ChessResultsCrawler.Logging;

/// <summary>
/// Mindest-Level und Overrides des Serilog-Bootstraps (Program.cs). Eigene Datei, damit ein Test die
/// Overrides am echten Logger pruefen kann (Codereview 2026-09-29, A10-014; Vorlage
/// rookhub/src/api/RookHub.Api/Logging/SerilogLevels.cs).
/// </summary>
public static class SerilogLevels
{
    /// <summary>Kategorie-Praefix → Mindest-Level (Serilog vergleicht praefixweise an Punktgrenzen).</summary>
    public static readonly IReadOnlyDictionary<string, LogEventLevel> Overrides = new Dictionary<string, LogEventLevel>
    {
        ["Microsoft.AspNetCore"] = LogEventLevel.Warning,
        ["Microsoft.EntityFrameworkCore"] = LogEventLevel.Warning,
        // Pro ausgehendem Request loggt Microsoft.Extensions.Http sonst 4 INF-Zeilen
        // (Start/Sending/Received/End) samt voller URL. Der Crawler schreibt je Abruf ohnehin seine
        // eigene "CrawlRequest"-Zeile (ChessResultsTransport) — wie in rookhub und piratechess nur
        // noch Warnungen.
        ["System.Net.Http.HttpClient"] = LogEventLevel.Warning,
    };

    /// <summary>Information als Grundlevel plus <see cref="Overrides"/>.</summary>
    public static LoggerConfiguration ApplyCrawlerLevels(this LoggerConfiguration configuration)
    {
        configuration.MinimumLevel.Information();
        foreach (var (source, level) in Overrides)
            configuration.MinimumLevel.Override(source, level);
        return configuration;
    }
}
