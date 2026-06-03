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
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, ex));
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
}
