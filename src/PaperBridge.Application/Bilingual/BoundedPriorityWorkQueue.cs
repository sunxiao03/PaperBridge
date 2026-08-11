namespace PaperBridge.Application.Bilingual;

public sealed class BoundedPriorityWorkQueue : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly PriorityQueue<WorkItem, (int Priority, long Sequence)> _queue = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _workers;
    private readonly int _maximumQueued;
    private long _sequence;
    private int _activeCount;
    private int _disposed;

    public BoundedPriorityWorkQueue(int maximumQueued = 32, int maximumConcurrency = 2)
    {
        if (maximumQueued is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueued));
        }

        if (maximumConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        _maximumQueued = maximumQueued;
        _workers = Enumerable.Range(0, maximumConcurrency).Select(_ => WorkerAsync()).ToArray();
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public int ActiveCount => Volatile.Read(ref _activeCount);

    public WorkQueueEnqueueResult TryEnqueue(
        string key,
        int priority,
        Func<CancellationToken, Task> work,
        CancellationToken ownerToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);
        lock (_sync)
        {
            if (_keys.Contains(key))
            {
                return WorkQueueEnqueueResult.Duplicate;
            }

            if (_queue.Count >= _maximumQueued)
            {
                return WorkQueueEnqueueResult.Full;
            }

            _keys.Add(key);
            _queue.Enqueue(
                new WorkItem(key, work, ownerToken),
                (priority, Interlocked.Increment(ref _sequence)));
        }

        _signal.Release();
        return WorkQueueEnqueueResult.Accepted;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _signal.Release(_workers.Length);
        try
        {
            await Task.WhenAll(_workers);
        }
        catch (OperationCanceledException)
        {
        }

        _signal.Dispose();
        _lifetime.Dispose();
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            await _signal.WaitAsync(_lifetime.Token);
            WorkItem? item;
            lock (_sync)
            {
                if (!_queue.TryDequeue(out item, out _))
                {
                    if (_lifetime.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }
            }

            Interlocked.Increment(ref _activeCount);
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.Token,
                    item.OwnerToken);
                await item.Work(cancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || item.OwnerToken.IsCancellationRequested)
            {
            }
            catch
            {
                // The owner reports individual work failures; workers must remain available.
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
                lock (_sync)
                {
                    _keys.Remove(item.Key);
                }
            }
        }
    }

    private sealed record WorkItem(
        string Key,
        Func<CancellationToken, Task> Work,
        CancellationToken OwnerToken);
}

public enum WorkQueueEnqueueResult
{
    Accepted,
    Duplicate,
    Full
}
