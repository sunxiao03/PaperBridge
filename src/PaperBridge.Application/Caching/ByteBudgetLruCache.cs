namespace PaperBridge.Application.Caching;

public sealed class ByteBudgetLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recency = [];
    private readonly Func<TValue, long> _sizeOf;
    private readonly Action<TValue>? _onEvicted;

    public ByteBudgetLruCache(long budgetBytes, Func<TValue, long> sizeOf, Action<TValue>? onEvicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentNullException.ThrowIfNull(sizeOf);

        BudgetBytes = budgetBytes;
        _sizeOf = sizeOf;
        _onEvicted = onEvicted;
    }

    public long BudgetBytes { get; }

    public long CurrentBytes { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public bool Set(TKey key, TValue value)
    {
        var size = _sizeOf(value);
        ArgumentOutOfRangeException.ThrowIfNegative(size);

        lock (_gate)
        {
            RemoveCore(key);

            if (size > BudgetBytes)
            {
                _onEvicted?.Invoke(value);
                return false;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, value, size));
            _entries.Add(key, node);
            _recency.AddFirst(node);
            CurrentBytes += size;

            while (CurrentBytes > BudgetBytes)
            {
                EvictLeastRecentlyUsed();
            }

            return true;
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            return RemoveCore(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            while (_recency.Last is not null)
            {
                EvictLeastRecentlyUsed();
            }
        }
    }

    private bool RemoveCore(TKey key)
    {
        if (!_entries.Remove(key, out var node))
        {
            return false;
        }

        _recency.Remove(node);
        CurrentBytes -= node.Value.Size;
        _onEvicted?.Invoke(node.Value.Value);
        return true;
    }

    private void EvictLeastRecentlyUsed()
    {
        var node = _recency.Last!;
        _recency.RemoveLast();
        _entries.Remove(node.Value.Key);
        CurrentBytes -= node.Value.Size;
        _onEvicted?.Invoke(node.Value.Value);
    }

    private sealed record Entry(TKey Key, TValue Value, long Size);
}

