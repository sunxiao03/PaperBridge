using System.Collections.Concurrent;
using PaperBridge.Application.Bilingual;

namespace PaperBridge.Domain.Tests.Bilingual;

public sealed class BoundedPriorityWorkQueueTests
{
    [Fact]
    public async Task Queue_RunsHigherPriorityPendingWorkFirstAndDeduplicatesKeys()
    {
        await using var queue = new BoundedPriorityWorkQueue(maximumQueued: 4, maximumConcurrency: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new ConcurrentQueue<string>();
        Assert.Equal(WorkQueueEnqueueResult.Accepted, queue.TryEnqueue("blocker", 0, async token =>
        {
            await gate.Task.WaitAsync(token);
            completed.Enqueue("blocker");
        }));
        await WaitUntilAsync(() => queue.ActiveCount == 1);

        Assert.Equal(WorkQueueEnqueueResult.Accepted, queue.TryEnqueue("low", 20, _ =>
        {
            completed.Enqueue("low");
            return Task.CompletedTask;
        }));
        Assert.Equal(WorkQueueEnqueueResult.Accepted, queue.TryEnqueue("high", 5, _ =>
        {
            completed.Enqueue("high");
            return Task.CompletedTask;
        }));
        Assert.Equal(WorkQueueEnqueueResult.Duplicate, queue.TryEnqueue("high", 1, _ => Task.CompletedTask));

        gate.SetResult();
        await WaitUntilAsync(() => completed.Count == 3);

        Assert.Equal(["blocker", "high", "low"], completed.ToArray());
    }

    [Fact]
    public async Task Queue_RejectsWorkBeyondPendingLimit()
    {
        await using var queue = new BoundedPriorityWorkQueue(maximumQueued: 1, maximumConcurrency: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue("active", 0, token => gate.Task.WaitAsync(token));
        await WaitUntilAsync(() => queue.ActiveCount == 1);

        Assert.Equal(WorkQueueEnqueueResult.Accepted, queue.TryEnqueue("pending", 1, _ => Task.CompletedTask));
        Assert.Equal(WorkQueueEnqueueResult.Full, queue.TryEnqueue("overflow", 1, _ => Task.CompletedTask));
        gate.SetResult();
    }

    [Fact]
    public async Task Queue_ObservesOwnerCancellation()
    {
        await using var queue = new BoundedPriorityWorkQueue(maximumQueued: 2, maximumConcurrency: 1);
        using var cancellation = new CancellationTokenSource();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue("cancel", 0, async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                observed.SetResult();
            }
        }, cancellation.Token);

        cancellation.Cancel();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => queue.ActiveCount == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
