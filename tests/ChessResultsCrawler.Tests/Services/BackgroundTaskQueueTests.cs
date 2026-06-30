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
