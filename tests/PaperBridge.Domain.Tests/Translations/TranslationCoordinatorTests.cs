using System.Collections.Concurrent;
using System.Net;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Translation;
using PaperBridge.Domain.Translations;

namespace PaperBridge.Domain.Tests.Translations;

public sealed class TranslationCoordinatorTests
{
    [Fact]
    public async Task TranslateAsync_CacheHitDoesNotCallProvider()
    {
        var cache = new MemoryCache();
        var provider = new DelegateProvider((_, _) => throw new InvalidOperationException("Provider should not run."));
        var job = CreateJob();
        await cache.SetAsync(
            job.CreateCacheKey(provider.ProviderId),
            new CachedTranslation("缓存译文", job.Model, 1, 2, DateTimeOffset.UtcNow));
        using var coordinator = CreateCoordinator(provider, cache);

        var result = await coordinator.TranslateAsync(job);

        Assert.True(result.IsCacheHit);
        Assert.Equal("缓存译文", result.Translation);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_ConcurrentIdenticalRequestsShareOneProviderCall()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return Response(request);
        });
        using var coordinator = CreateCoordinator(provider, new MemoryCache());

        var tasks = Enumerable.Range(0, 10).Select(_ => coordinator.TranslateAsync(CreateJob())).ToArray();
        await WaitUntilAsync(() => provider.CallCount == 1);
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, provider.CallCount);
        Assert.All(results, result => Assert.Equal("测试译文", result.Translation));
    }

    [Fact]
    public async Task TranslateAsync_CancelsProviderWhenAllJoinedCallersCancel()
    {
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                providerCancelled.TrySetResult();
                throw;
            }
        });
        using var coordinator = CreateCoordinator(provider, new MemoryCache());
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = coordinator.TranslateAsync(CreateJob(), firstCancellation.Token);
        var second = coordinator.TranslateAsync(CreateJob(), secondCancellation.Token);
        await WaitUntilAsync(() => provider.CallCount == 1);

        firstCancellation.Cancel();
        secondCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TranslateAsync_EnforcesConfiguredConcurrencyLimit()
    {
        var active = 0;
        var maximumActive = 0;
        var provider = new DelegateProvider(async (request, token) =>
        {
            var current = Interlocked.Increment(ref active);
            SetMaximum(ref maximumActive, current);
            try
            {
                await Task.Delay(25, token);
                return Response(request);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        using var coordinator = CreateCoordinator(provider, new MemoryCache(), maxConcurrency: 2);

        var tasks = Enumerable.Range(0, 8)
            .Select(index => coordinator.TranslateAsync(CreateJob($"source {index}")))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(2, maximumActive);
    }

    [Fact]
    public async Task TranslateAsync_RetriesTransientRateLimitAndNetworkErrors()
    {
        var attempt = 0;
        var provider = new DelegateProvider((request, _) =>
        {
            return Interlocked.Increment(ref attempt) switch
            {
                1 => throw new TranslationProviderException("rate limited", HttpStatusCode.TooManyRequests, true),
                2 => throw new HttpRequestException("offline"),
                _ => Task.FromResult(Response(request))
            };
        });
        using var coordinator = CreateCoordinator(provider, new MemoryCache());

        var result = await coordinator.TranslateAsync(CreateJob());

        Assert.Equal("测试译文", result.Translation);
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_DoesNotRetryAuthenticationFailure()
    {
        var provider = new DelegateProvider((_, _) => throw new TranslationProviderException(
            "authentication failed",
            HttpStatusCode.Unauthorized,
            isTransient: false));
        using var coordinator = CreateCoordinator(provider, new MemoryCache());

        var exception = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            coordinator.TranslateAsync(CreateJob()));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task TranslateAsync_ReportsOverallTimeout()
    {
        var provider = new DelegateProvider(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var coordinator = new TranslationCoordinator(
            provider,
            new MemoryCache(),
            new TranslationExecutionOptions(1, TimeSpan.FromMilliseconds(40), RetryDelay: TimeSpan.Zero));

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.TranslateAsync(CreateJob()));
    }

    [Fact]
    public async Task Dispose_CancelsInFlightProviderWork()
    {
        var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegateProvider(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                providerCancelled.TrySetResult();
                throw;
            }
        });
        var coordinator = CreateCoordinator(provider, new MemoryCache());
        var request = coordinator.TranslateAsync(CreateJob());
        await WaitUntilAsync(() => provider.CallCount == 1);

        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static TranslationCoordinator CreateCoordinator(
        ITranslationProvider provider,
        ITranslationCache cache,
        int maxConcurrency = 2) =>
        new(
            provider,
            cache,
            new TranslationExecutionOptions(
                maxConcurrency,
                TimeSpan.FromSeconds(2),
                RetryDelay: TimeSpan.FromMilliseconds(1)),
            nextJitter: () => 0);

    private static TranslationJob CreateJob(string sourceText = "test source") =>
        new(
            "document-hash",
            sourceText,
            "test-model",
            TranslationGranularity.Selection,
            Context: string.Empty,
            Terminology: new Dictionary<string, string>(),
            GlossaryVersion: "g1",
            CustomInstructionVersion: "none");

    private static TranslationResponse Response(TranslationRequest request) =>
        new("测试译文", request.Model, 3, 4);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static void SetMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed class DelegateProvider(
        Func<TranslationRequest, CancellationToken, Task<TranslationResponse>> translate) : ITranslationProvider
    {
        private int _callCount;

        public string ProviderId => "test-provider";

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<TranslationResponse> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return translate(request, cancellationToken);
        }
    }

    private sealed class MemoryCache : ITranslationCache
    {
        private readonly ConcurrentDictionary<string, CachedTranslation> _entries = new();

        public Task<CachedTranslation?> GetAsync(
            TranslationCacheKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.TryGetValue(key.ToStableId(), out var value);
            return Task.FromResult(value);
        }

        public Task SetAsync(
            TranslationCacheKey key,
            CachedTranslation translation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries[key.ToStableId()] = translation;
            return Task.CompletedTask;
        }
    }
}
