using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChessResultsCrawler.Tests.Services;

public class HeartbeatServiceTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public List<Dictionary<string, object?>> Properties { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, ex));
            Properties.Add(state is IEnumerable<KeyValuePair<string, object?>> kv
                ? kv.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>());
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task EmitAsync_LogsStructuredHealthyHeartbeat_WhenDbReachable()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("hb-" + Guid.NewGuid()));
        using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger<HeartbeatService>();
        var config = new ConfigurationBuilder().Build();

        var svc = new HeartbeatService(provider.GetRequiredService<IServiceScopeFactory>(), logger, config);
        await svc.EmitAsync();

        Assert.Single(logger.Messages);
        Assert.Contains("Heartbeat", logger.Messages[0]);
        Assert.Contains(HeartbeatService.ServiceName, logger.Messages[0]);   // rookhub-crawler
        Assert.Contains("healthy", logger.Messages[0]);
    }

    /// <summary>
    /// Vertrag mit dem log-watcher (I2-012): er erkennt den Crawler am strukturierten Feld
    /// labels.HeartbeatService = "rookhub-crawler" bzw. (Altform in der Prod-Konfig) per match_phrase
    /// am Satz "Heartbeat: rookhub-crawler". Beides ist hier wörtlich gepinnt — eine Umformulierung des
    /// Templates oder ein neuer Dienstname fällt hier auf statt als heartbeat_missing HIGH auf Prod.
    /// </summary>
    [Fact]
    public async Task EmitAsync_RendersTheExactSentenceAndFieldTheLogWatcherLooksFor()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("hb-" + Guid.NewGuid()));
        using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger<HeartbeatService>();

        await new HeartbeatService(provider.GetRequiredService<IServiceScopeFactory>(), logger,
            new ConfigurationBuilder().Build()).EmitAsync();

        Assert.StartsWith("Heartbeat: rookhub-crawler ", Assert.Single(logger.Messages));
        var props = Assert.Single(logger.Properties);
        Assert.Equal("rookhub-crawler", props["HeartbeatService"]);
        Assert.StartsWith("Heartbeat: {HeartbeatService} ", (string?)props["{OriginalFormat}"]);
    }
}
