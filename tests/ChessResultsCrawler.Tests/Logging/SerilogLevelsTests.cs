using ChessResultsCrawler.Logging;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ChessResultsCrawler.Tests.Logging;

/// <summary>
/// Overrides des Serilog-Bootstraps am echten Logger (Codereview 2026-09-29, A10-014): die Logging-Handler von
/// Microsoft.Extensions.Http schrieben je ausgehendem Request 4 INF-Zeilen samt URL ins ES — rookhub und
/// piratechess daempfen sie, der Crawler jetzt auch. Eigene Zeilen (CrawlRequest) bleiben auf Information.
/// </summary>
public class SerilogLevelsTests
{
    private sealed class Sammler : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Microsoft.Extensions.Logging.ILoggerFactory Factory, Sammler Sink) Factory()
    {
        var sink = new Sammler();
        var logger = new LoggerConfiguration().ApplyCrawlerLevels().WriteTo.Sink(sink).CreateLogger();
        return (new SerilogLoggerFactory(logger, dispose: true), sink);
    }

    [Theory]
    // Kategorien, unter denen IHttpClientFactory loggt (typisierter bzw. Standard-Client).
    [InlineData("System.Net.Http.HttpClient.CrawlerService.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.Default.ClientHandler")]
    public void HttpClientHandler_InformationFaelltWeg_WarnungBleibt(string category)
    {
        var (factory, sink) = Factory();
        using (factory)
        {
            var log = factory.CreateLogger(category);
            log.LogInformation("Sending HTTP request GET https://chess-results.com/tnr1.aspx?lan=1&art=9&snr=4");
            log.LogWarning("upstream kaputt");
        }
        var e = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Warning, e.Level);
    }

    [Fact]
    public void EigeneDienste_BleibenAufInformation_AspNetCoreErstAbWarning()
    {
        var (factory, sink) = Factory();
        using (factory)
        {
            factory.CreateLogger("ChessResultsCrawler.Services.CrawlerService").LogInformation("CrawlRequest …");
            factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("Request starting");
            factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("Executed DbCommand");
        }
        var e = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Information, e.Level);
    }

    [Fact]
    public void HttpClientOverride_WieInRookhub()
    {
        // Spiegel von rookhub/src/api/RookHub.Api/Logging/SerilogLevels.cs
        Assert.Equal(LogEventLevel.Warning, SerilogLevels.Overrides["System.Net.Http.HttpClient"]);
    }
}
