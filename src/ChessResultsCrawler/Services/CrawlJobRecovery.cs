using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Räumt beim Service-Start verwaiste Crawl-Jobs auf. Die in-memory Job-Queue
/// (<see cref="BackgroundTaskWorker"/>/<see cref="BackgroundTaskQueue"/>) ist nach einem
/// (Neu-)Start leer, aber in der DB können Jobs aus dem vorigen Prozess noch auf
/// <see cref="CrawlJobStatus.Queued"/>/<see cref="CrawlJobStatus.Running"/> stehen
/// (Crash/Deploy mitten im Lauf, oder Enqueue nach dem SaveChanges fehlgeschlagen).
/// Da deren computed <c>ActiveKey</c> die ChessResultsId enthält und unique ist, würde ein
/// solcher „Zombie" jeden künftigen Crawl desselben Turniers dauerhaft mit 409 blockieren.
/// Darum beim Start alle aktiven Jobs als <see cref="CrawlJobStatus.Failed"/> markieren —
/// sie werden von keinem Worker mehr bedient.
/// </summary>
public static class CrawlJobRecovery
{
    public const string StaleMessage = "Abgebrochen durch Service-Neustart (verwaister Job).";

    /// <summary>Setzt alle Queued/Running-Jobs auf Failed und gibt die Anzahl zurück.</summary>
    public static async Task<int> RecoverStaleJobsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var stale = await db.CrawlJobs
            .Where(j => j.Status == CrawlJobStatus.Queued || j.Status == CrawlJobStatus.Running)
            .ToListAsync(ct);
        if (stale.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var job in stale)
        {
            job.Status = CrawlJobStatus.Failed;
            job.ErrorMessage = StaleMessage;
            job.CompletedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }
}
