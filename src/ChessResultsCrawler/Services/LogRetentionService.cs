using ChessResultsCrawler.Data;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Services;

public class LogRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LogRetentionService> _logger;
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public LogRetentionService(IServiceScopeFactory scopeFactory, ILogger<LogRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow - RetentionPeriod;

                var deletedRequests = await db.RequestLogs
                    .Where(l => l.Timestamp < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                var deletedCrawlRequests = await db.CrawlRequestLogs
                    .Where(l => l.Timestamp < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deletedRequests > 0 || deletedCrawlRequests > 0)
                    _logger.LogInformation("LogRetention: Deleted {RequestLogs} request logs and {CrawlLogs} crawl request logs older than {Cutoff}",
                        deletedRequests, deletedCrawlRequests, cutoff);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "LogRetention cleanup failed");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
