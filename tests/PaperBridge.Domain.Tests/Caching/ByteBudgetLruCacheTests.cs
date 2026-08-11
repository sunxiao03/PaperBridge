using PaperBridge.Application.Caching;

namespace PaperBridge.Domain.Tests.Caching;

public sealed class ByteBudgetLruCacheTests
{
    [Fact]
    public void Set_EvictsLeastRecentlyUsedEntriesUntilWithinBudget()
    {
        var cache = new ByteBudgetLruCache<string, byte[]>(10, value => value.LongLength);
        cache.Set("first", new byte[4]);
        cache.Set("second", new byte[4]);
        Assert.True(cache.TryGet("first", out _));

        cache.Set("third", new byte[4]);

        Assert.False(cache.TryGet("second", out _));
        Assert.True(cache.TryGet("first", out _));
        Assert.True(cache.TryGet("third", out _));
        Assert.Equal(8, cache.CurrentBytes);
    }

    [Fact]
    public void Set_RejectsSingleEntryLargerThanBudget()
    {
        var evicted = 0;
        var cache = new ByteBudgetLruCache<string, byte[]>(
            10,
            value => value.LongLength,
            _ => evicted++);

        var accepted = cache.Set("oversized", new byte[11]);

        Assert.False(accepted);
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, evicted);
    }
}
