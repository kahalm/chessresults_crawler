using System.Threading.Channels;

namespace ChessResultsCrawler.Services;

public interface IBackgroundTaskQueue
{
    ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> workItem);
    bool TryEnqueue(Func<IServiceProvider, CancellationToken, Task> workItem);
    ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}

public class BackgroundTaskQueue : IBackgroundTaskQueue
{
    /// <summary>
    /// Vorgabe-Kapazitaet. RookHub stoesst nach jedem API-Start den Refresh ALLER abonnierten
    /// Turniere auf einmal an (25.09.: 84 Stueck) — die Schlange muss so einen Schwall fassen.
    /// Die Auftraege sind kleine Closures, der Speicher spielt keine Rolle.
    /// </summary>
    public const int DefaultCapacity = 500;

    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _queue;

    public BackgroundTaskQueue(int capacity = DefaultCapacity)
    {
        // Wait, NICHT DropWrite: bei DropWrite meldet TryWrite auch bei voller Schlange Erfolg
        // und wirft den Auftrag weg. Der Controller hielt den Job dann fuer angenommen, seine
        // DB-Zeile blieb „Queued" und sperrte das Turnier als „already running" (409) bis zum
        // naechsten Neustart — mit der alten Kapazitaet 5 kamen so von 84 Turnieren 74 eine
        // Woche lang nie dran. Mit Wait liefert TryWrite bei voller Schlange false, und die
        // vorhandene 429-Behandlung (Job auf Failed, Sperre frei) greift wie gedacht.
        _queue = Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
    }

    public async ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> workItem)
    {
        await _queue.Writer.WriteAsync(workItem);
    }

    public bool TryEnqueue(Func<IServiceProvider, CancellationToken, Task> workItem)
    {
        return _queue.Writer.TryWrite(workItem);
    }

    public async ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}

public class BackgroundTaskWorker : BackgroundService
{
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VpnReadinessGate _vpnGate;
    private readonly ILogger<BackgroundTaskWorker> _logger;

    public BackgroundTaskWorker(IBackgroundTaskQueue queue, IServiceScopeFactory scopeFactory,
        VpnReadinessGate vpnGate, ILogger<BackgroundTaskWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _vpnGate = vpnGate;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workItem = await _queue.DequeueAsync(stoppingToken);

            // Vor dem ersten Crawl auf den VPN-Tunnel warten (No-Op ohne VPN bzw. nach erstem
            // Erreichen der Bereitschaft). Verhindert Connection-Level-Fehler direkt nach einem
            // (Re-)Deploy, wenn der Tunnel noch nicht wieder verbunden ist.
            try
            {
                await _vpnGate.WaitUntilReadyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                await workItem(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background task failed");
            }
        }
    }
}
