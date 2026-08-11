using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaperBridge.Application.Abstractions;
using PaperBridge.Domain.Translations;

namespace PaperBridge.Application.Translation;

public sealed class TranslationCoordinator : IDisposable
{
    public const string PromptVersion = "academic-en-zh-v1";
    private readonly ITranslationProvider _provider;
    private readonly ITranslationCache _cache;
    private readonly TranslationExecutionOptions _options;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, InFlightTranslation> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<double> _nextJitter;
    private int _disposed;
    private int _resourcesDisposed;

    public TranslationCoordinator(
        ITranslationProvider provider,
        ITranslationCache cache,
        TranslationExecutionOptions options,
        Func<double>? nextJitter = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _provider = provider;
        _cache = cache;
        _options = options;
        _concurrency = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        _nextJitter = nextJitter ?? Random.Shared.NextDouble;
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(job);
        job.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var key = job.CreateCacheKey(_provider.ProviderId);
        var cached = await _cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return new TranslationResult(
                cached.Translation,
                cached.Model,
                cached.InputTokens,
                cached.OutputTokens,
                IsCacheHit: true);
        }

        var stableId = key.ToStableId();
        InFlightTranslation operation;
        while (true)
        {
            if (_inFlight.TryGetValue(stableId, out operation!))
            {
                break;
            }

            var candidate = new InFlightTranslation();
            if (!_inFlight.TryAdd(stableId, candidate))
            {
                candidate.Dispose();
                continue;
            }

            operation = candidate;
            operation.Start(token => ExecuteWithCacheRecheckAsync(job, key, token));
            _ = operation.Task.ContinueWith(
                _ =>
                {
                    _inFlight.TryRemove(new KeyValuePair<string, InFlightTranslation>(stableId, operation));
                    operation.MarkCompleted();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            break;
        }

        return await operation.JoinAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        foreach (var operation in _inFlight.Values)
        {
            operation.Cancel();
        }

        var tasks = _inFlight.Values.Select(operation => operation.Task).ToArray();
        if (tasks.Length == 0)
        {
            DisposeResources();
            return;
        }

        _ = System.Threading.Tasks.Task.WhenAll(tasks).ContinueWith(
            task =>
            {
                _ = task.Exception;
                DisposeResources();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<TranslationResult> ExecuteAndCacheAsync(
        TranslationJob job,
        TranslationCacheKey key,
        CancellationToken operationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken, _lifetime.Token);
        cancellation.CancelAfter(_options.RequestTimeout);
        var token = cancellation.Token;

        try
        {
            await _concurrency.WaitAsync(token);
            try
            {
                TranslationResponse response;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        response = await _provider.TranslateAsync(job.ToRequest(), token);
                        break;
                    }
                    catch (TranslationProviderException exception) when (
                        exception.IsTransient && attempt < _options.MaximumAttempts)
                    {
                        await DelayBeforeRetryAsync(attempt, token);
                    }
                    catch (HttpRequestException) when (attempt < _options.MaximumAttempts)
                    {
                        await DelayBeforeRetryAsync(attempt, token);
                    }
                }

                var cached = new CachedTranslation(
                    response.Translation,
                    response.Model,
                    response.InputTokens,
                    response.OutputTokens,
                    DateTimeOffset.UtcNow);
                await _cache.SetAsync(key, cached, token);
                return new TranslationResult(
                    response.Translation,
                    response.Model,
                    response.InputTokens,
                    response.OutputTokens,
                    IsCacheHit: false);
            }
            finally
            {
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException exception) when (
            !operationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"翻译请求超过 {_options.RequestTimeout.TotalSeconds:F0} 秒。", exception);
        }
    }

    private async Task<TranslationResult> ExecuteWithCacheRecheckAsync(
        TranslationJob job,
        TranslationCacheKey key,
        CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return new TranslationResult(
                cached.Translation,
                cached.Model,
                cached.InputTokens,
                cached.OutputTokens,
                IsCacheHit: true);
        }

        return await ExecuteAndCacheAsync(job, key, cancellationToken);
    }

    private Task DelayBeforeRetryAsync(int failedAttempt, CancellationToken cancellationToken)
    {
        var exponential = _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, failedAttempt - 1);
        var jitter = exponential * 0.25 * Math.Clamp(_nextJitter(), 0, 1);
        return Task.Delay(TimeSpan.FromMilliseconds(exponential + jitter), cancellationToken);
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _lifetime.Dispose();
            _concurrency.Dispose();
        }
    }

    private sealed class InFlightTranslation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<TranslationResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;
        private int _disposed;
        private int _started;
        private int _waiterCount;

        public Task<TranslationResult> Task => _completion.Task;

        public void Start(Func<CancellationToken, Task<TranslationResult>> operation)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("Operation has already started.");
            }

            _ = RunAsync(operation);
        }

        public async Task<TranslationResult> JoinAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waiterCount);
            try
            {
                return await Task.WaitAsync(cancellationToken);
            }
            finally
            {
                if (Interlocked.Decrement(ref _waiterCount) == 0 && !Task.IsCompleted)
                {
                    Cancel();
                }

                TryDispose();
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion and coordinator disposal may race after the final waiter leaves.
            }
        }

        public void MarkCompleted()
        {
            Volatile.Write(ref _completed, 1);
            TryDispose();
        }

        public void Dispose()
        {
            Volatile.Write(ref _completed, 1);
            TryDispose();
        }

        private void TryDispose()
        {
            if (Volatile.Read(ref _completed) != 0 &&
                Volatile.Read(ref _waiterCount) == 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellation.Dispose();
            }
        }

        private async Task RunAsync(Func<CancellationToken, Task<TranslationResult>> operation)
        {
            try
            {
                _completion.TrySetResult(await operation(_cancellation.Token));
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}

public sealed record TranslationExecutionOptions(
    int MaxConcurrency,
    TimeSpan RequestTimeout,
    int MaximumAttempts = 4,
    TimeSpan? RetryDelay = null)
{
    public TimeSpan InitialRetryDelay => RetryDelay ?? TimeSpan.FromMilliseconds(500);

    public void Validate()
    {
        if (MaxConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaximumAttempts is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        }

        if (InitialRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        }
    }
}

public sealed record TranslationJob(
    string DocumentHash,
    string SourceText,
    string Model,
    TranslationGranularity Granularity,
    string Context,
    IReadOnlyDictionary<string, string> Terminology,
    string GlossaryVersion,
    string CustomInstructionVersion,
    string? CustomInstruction = null,
    string SourceLanguage = "en",
    string TargetLanguage = "zh-CN")
{
    public TranslationCacheKey CreateCacheKey(string providerId) =>
        new(
            DocumentHash,
            SourceText,
            providerId,
            Model,
            TranslationCoordinator.PromptVersion,
            GlossaryVersion,
            Granularity,
            CustomInstructionVersion,
            SourceLanguage,
            TargetLanguage);

    public TranslationRequest ToRequest() =>
        new(
            SourceText,
            Model,
            Context,
            Terminology,
            CustomInstruction,
            Granularity,
            SourceLanguage,
            TargetLanguage);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DocumentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(GlossaryVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(CustomInstructionVersion);
        ArgumentNullException.ThrowIfNull(Terminology);
        if (SourceText.Length > 100_000)
        {
            throw new ArgumentException("Source text exceeds 100,000 characters.", nameof(SourceText));
        }

        if (Context?.Length > 20_000)
        {
            throw new ArgumentException("Context exceeds 20,000 characters.", nameof(Context));
        }

        if (CustomInstruction?.Length > 8_000)
        {
            throw new ArgumentException("Custom instruction exceeds 8,000 characters.", nameof(CustomInstruction));
        }

        if (Terminology.Count > 1_000 || Terminology.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new ArgumentException("Terminology constraints are invalid.", nameof(Terminology));
        }
    }

    public static string VersionCustomInstruction(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return "none";
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instruction.Trim())));
    }
}

public sealed record TranslationResult(
    string Translation,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    bool IsCacheHit);
