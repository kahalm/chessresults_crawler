using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Aufbewahrungsfristen der Crawler-DB (Entscheidung S3-018): ein Löschlauf beim Start, danach
/// einmal am Tag.
/// <list type="bullet">
/// <item>Abgeschlossene/fehlgeschlagene <see cref="CrawlJob"/>s 30 Tage nach ihrem Ende. RookHub
/// fragt den Status eines Auftrags nur in den Minuten nach dem Anstoß ab; aktive Aufträge
/// (Queued/Running) löscht der Lauf nie.</item>
/// <item><see cref="PlayerClub"/>-Zeilen (FIDE-ID → Verein eines Dritten), die seit 180 Tagen
/// niemand aufgefrischt hat. Nach <see cref="ClubService.LookupTtl"/> gelten sie ohnehin als
/// veraltet; „Vereine nachtragen" sucht den Spieler dann neu.</item>
/// </list>
/// <para>Turniere und Spieler bleiben: RookHub verweist über <c>CrawlerTournamentId</c> auf sie,
/// ihre Frist ist ein eigener Folgepunkt. Die Crawler-DB liegt nicht im Ersatz-Backup — was hier
/// gelöscht wird, ist weg.</para>
/// </summary>
public class RetentionService : BackgroundService
{
    public static readonly TimeSpan CrawlJobRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan PlayerClubRetention = TimeSpan.FromDays(180);
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IServiceScopeFactory scopeFactory, ILogger<RetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* Shutdown */ }
    }

    /// <summary>Ein Löschlauf mit eigenem Scope. Wirft nie — ein Fehler wartet auf den nächsten Tag.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (jobs, clubs) = await PurgeAsync(db, DateTime.UtcNow, ct);
            if (jobs > 0 || clubs > 0)
                _logger.LogInformation(
                    "Aufbewahrung: {DeletedCrawlJobs} alte Crawl-Aufträge und {DeletedPlayerClubs} alte Vereins-Suchergebnisse gelöscht",
                    jobs, clubs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mitten im Lauf.
        }
        catch (Exception ex)
        {
            // Eine Ausnahme aus ExecuteAsync würde den ganzen Host beenden.
            _logger.LogWarning(ex, "Aufbewahrung: Löschlauf fehlgeschlagen");
        }
    }

    /// <summary>Löscht, was die Fristen überschritten hat, und gibt die Anzahl je Tabelle zurück.</summary>
    public static async Task<(int CrawlJobs, int PlayerClubs)> PurgeAsync(
        AppDbContext db, DateTime utcNow, CancellationToken ct = default)
    {
        var jobCutoff = utcNow - CrawlJobRetention;
        var clubCutoff = utcNow - PlayerClubRetention;

        // CompletedAt fehlt nur bei Altzeilen; dann zählt das Anlegen.
        var jobs = await DeleteAsync(db, db.CrawlJobs.Where(j =>
            (j.Status == CrawlJobStatus.Completed || j.Status == CrawlJobStatus.Failed)
            && (j.CompletedAt ?? j.CreatedAt) < jobCutoff), ct);
        var clubs = await DeleteAsync(db, db.PlayerClubs.Where(c => c.FetchedAt < clubCutoff), ct);
        return (jobs, clubs);
    }

    private static async Task<int> DeleteAsync<T>(AppDbContext db, IQueryable<T> rows, CancellationToken ct)
        where T : class
    {
        if (db.Database.IsRelational())
            return await rows.ExecuteDeleteAsync(ct);

        // InMemory (Tests) kennt kein ExecuteDelete — dieselbe Auswahl, Zeile für Zeile.
        var list = await rows.ToListAsync(ct);
        db.Set<T>().RemoveRange(list);
        await db.SaveChangesAsync(ct);
        return list.Count;
    }
}
