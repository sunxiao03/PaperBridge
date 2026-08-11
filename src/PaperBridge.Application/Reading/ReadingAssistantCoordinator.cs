using System.Net;
using PaperBridge.Application.Abstractions;

namespace PaperBridge.Application.Reading;

public sealed class ReadingAssistantCoordinator : IDisposable
{
    public const string PromptVersion = "reading-assistant-v1";
    private readonly IReadingAssistantProvider _provider;
    private readonly IReadingAssistantCache _cache;
    private readonly SemaphoreSlim _concurrency;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maximumAttempts;
    private readonly CancellationTokenSource _lifetime = new();
    private int _activeOperations;
    private int _disposed;
    private int _resourcesDisposed;

    public ReadingAssistantCoordinator(
        IReadingAssistantProvider provider,
        IReadingAssistantCache cache,
        int maximumConcurrency = 2,
        TimeSpan? requestTimeout = null,
        int maximumAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(cache);
        if (maximumConcurrency is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(90);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        if (maximumAttempts is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _provider = provider;
        _cache = cache;
        _maximumAttempts = maximumAttempts;
        _concurrency = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    public async Task<ReadingAssistantResult> CompleteAsync(
        ReadingAssistantJob job,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Increment(ref _activeOperations);
        if (Volatile.Read(ref _disposed) != 0)
        {
            ExitOperation();
            throw new ObjectDisposedException(nameof(ReadingAssistantCoordinator));
        }

        try
        {
            return await CompleteCoreAsync(job, cancellationToken);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task<ReadingAssistantResult> CompleteCoreAsync(
        ReadingAssistantJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var key = CreateCacheKey(job);
        if (job.Cacheable && await _cache.GetAsync(key, cancellationToken) is { } cached)
        {
            ValidateResponseLength(cached.Content);
            return new ReadingAssistantResult(
                cached.Content, cached.Model, cached.InputTokens, cached.OutputTokens, true);
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        requestCancellation.CancelAfter(_requestTimeout);
        var token = requestCancellation.Token;
        try
        {
            await _concurrency.WaitAsync(token);
            try
            {
                ReadingAssistantResponse response;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        response = await _provider.CompleteAsync(new ReadingAssistantRequest(
                            job.TaskKind,
                            job.Model,
                            job.SystemPrompt,
                            job.UserPrompt,
                            job.MaximumOutputTokens), token);
                        break;
                    }
                    catch (TranslationProviderException exception) when (
                        exception.IsTransient && attempt < _maximumAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1)), token);
                    }
                    catch (HttpRequestException) when (attempt < _maximumAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1)), token);
                    }
                }

                var result = new ReadingAssistantResult(
                    response.Content.Trim(), response.Model, response.InputTokens, response.OutputTokens, false);
                ValidateResponseLength(result.Content);
                if (job.Cacheable)
                {
                    await _cache.SetAsync(key, new CachedReadingAssistantResult(
                        result.Content,
                        result.Model,
                        result.InputTokens,
                        result.OutputTokens,
                        DateTimeOffset.UtcNow), token);
                }

                return result;
            }
            finally
            {
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"AI 阅读请求超过 {_requestTimeout.TotalSeconds:F0} 秒。", exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (Volatile.Read(ref _activeOperations) == 0)
        {
            DisposeResources();
        }
    }

    private void ExitOperation()
    {
        if (Interlocked.Decrement(ref _activeOperations) == 0 && Volatile.Read(ref _disposed) != 0)
        {
            DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _lifetime.Dispose();
            _concurrency.Dispose();
        }
    }

    private ReadingAssistantCacheKey CreateCacheKey(ReadingAssistantJob job) => new(
        job.DocumentHash,
        job.TaskKind,
        ReadingAssistantCacheKey.HashInput(job.SystemPrompt, job.UserPrompt),
        _provider.ProviderId,
        job.Model,
        PromptVersion,
        job.CustomInstructionVersion);

    private static void ValidateResponseLength(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 100_000)
        {
            throw new InvalidOperationException("AI response is empty or exceeds 100,000 characters.");
        }
    }
}
