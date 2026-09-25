using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class BackgroundTaskQueueTests
{
    [Fact]
    public async Task EnqueueDequeue_PreservesFifoOrder()
    {
        var q = new BackgroundTaskQueue(capacity: 5);
        var order = new List<int>();
        await q.EnqueueAsync((_, _) => { order.Add(1); return Task.CompletedTask; });
        await q.EnqueueAsync((_, _) => { order.Add(2); return Task.CompletedTask; });
        await q.EnqueueAsync((_, _) => { order.Add(3); return Task.CompletedTask; });

        for (var i = 0; i < 3; i++)
        {
            var item = await q.DequeueAsync(CancellationToken.None);
            await item(null!, CancellationToken.None);
        }

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public void TryEnqueue_UnderCapacity_ReturnsTrue()
    {
        var q = new BackgroundTaskQueue(capacity: 2);
        Assert.True(q.TryEnqueue((_, _) => Task.CompletedTask));
        Assert.True(q.TryEnqueue((_, _) => Task.CompletedTask));
    }

    /// <summary>
    /// Die Schlange darf einen Auftrag nie still verschlucken. Mit <c>DropWrite</c> meldete
    /// <c>TryWrite</c> auch bei voller Schlange Erfolg und warf den Auftrag weg: der Controller
    /// hielt den Job fuer angenommen, die DB-Zeile blieb „Queued" — und blockierte als
    /// „already running" (409) jeden weiteren Versuch fuer dieses Turnier bis zum naechsten
    /// Neustart. Beobachtet auf Prod (25.09.): von 84 Turnieren kamen 74 in einer Woche nie dran.
    /// </summary>
    [Fact]
    public async Task TryEnqueue_WhenFull_ReturnsFalse_AndDropsNothingThatWasAccepted()
    {
        var q = new BackgroundTaskQueue(capacity: 3);
        var ran = new List<int>();
        var accepted = new List<int>();
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            if (q.TryEnqueue((_, _) => { ran.Add(n); return Task.CompletedTask; }))
                accepted.Add(n);
        }

        // Voll ist voll: der 4. und 5. Versuch muessen abgewiesen werden, damit der Aufrufer
        // es merkt (429 + Job auf Failed) — nicht still verworfen.
        Assert.Equal(new[] { 1, 2, 3 }, accepted);

        for (var i = 0; i < accepted.Count; i++)
        {
            var item = await q.DequeueAsync(CancellationToken.None);
            await item(null!, CancellationToken.None);
        }
        Assert.Equal(accepted, ran);   // alles Angenommene laeuft auch
    }

    [Fact]
    public void DefaultCapacity_HoldsAStartupBurst()
    {
        // RookHub stoesst nach jedem API-Start den Refresh ALLER abonnierten Turniere an —
        // am 25.09. waren das 84 auf einen Schlag. Die Vorgabe muss so einen Schwall fassen.
        var q = new BackgroundTaskQueue();
        for (var i = 0; i < 200; i++)
            Assert.True(q.TryEnqueue((_, _) => Task.CompletedTask), $"Auftrag {i + 1} abgewiesen");
    }

    [Fact]
    public async Task DequeueAsync_ReturnsTheEnqueuedDelegate()
    {
        var q = new BackgroundTaskQueue(capacity: 1);
        var ran = false;
        q.TryEnqueue((_, _) => { ran = true; return Task.CompletedTask; });

        var item = await q.DequeueAsync(CancellationToken.None);
        await item(null!, CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public async Task DequeueAsync_HonorsCancellationWhenEmpty()
    {
        var q = new BackgroundTaskQueue(capacity: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await q.DequeueAsync(cts.Token));
    }
}
