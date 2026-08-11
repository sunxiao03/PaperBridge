using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Translation;
using PaperBridge.Domain.Translations;
using PaperBridge.Infrastructure.Storage;
using PaperBridge.Infrastructure.Translation;

namespace PaperBridge.Infrastructure.Tests.Translation;

public sealed class TranslationPipelineTests
{
    [Fact]
    public async Task Provider_SendsOpenAiCompatiblePayloadAndParsesUsageWithoutLeakingKey()
    {
        const string apiKey = "unit-test-sensitive-value";
        string? authorization = null;
        string? payload = null;
        Uri? requestUri = null;
        var handler = new DelegateHandler(async (request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            requestUri = request.RequestUri;
            payload = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "model": "returned-model",
                  "choices": [{"message": {"content": "有效增殖因子为一。"}}],
                  "usage": {"prompt_tokens": 12, "completion_tokens": 8}
                }
                """);
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            client,
            "openai",
            "https://api.example.test/v1/",
            apiKey);

        var response = await provider.TranslateAsync(new TranslationRequest(
            "The effective multiplication factor k_eff = 1.0.",
            "configured-model",
            "Reactor physics paper",
            new Dictionary<string, string> { ["effective multiplication factor"] = "有效增殖因子" },
            "Use formal academic Chinese.",
            TranslationGranularity.Sentence));

        Assert.Equal("Bearer unit-test-sensitive-value", authorization);
        Assert.Equal("https://api.example.test/v1/chat/completions", requestUri?.AbsoluteUri);
        Assert.Contains("do not translate formulas", payload, StringComparison.Ordinal);
        Assert.Contains("effective multiplication factor", payload, StringComparison.Ordinal);
        Assert.Contains("k_eff = 1.0", payload, StringComparison.Ordinal);
        Assert.Equal("有效增殖因子为一。", response.Translation);
        Assert.Equal("returned-model", response.Model);
        Assert.Equal(12, response.InputTokens);
        Assert.Equal(8, response.OutputTokens);
        Assert.DoesNotContain(apiKey, payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Provider_ClassifiesHttpFailuresWithoutIncludingResponseBody(
        HttpStatusCode statusCode,
        bool transient)
    {
        const string sensitiveBody = "sensitive-response-body";
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(sensitiveBody)
        })));
        var provider = new OpenAiCompatibleTranslationProvider(
            client,
            "deepseek",
            "https://api.deepseek.example/v1/",
            "test-secret");

        var exception = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest()));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(transient, exception.IsTransient);
        Assert.DoesNotContain(sensitiveBody, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pipeline_DeduplicatesConcurrentHttpRequestsAndPersistsCache()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new DelegateHandler(async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return JsonResponse(SuccessJson);
            });
            using var client = new HttpClient(handler);
            var provider = new OpenAiCompatibleTranslationProvider(
                client,
                "openai",
                "https://api.example.test/v1/",
                "test-secret");
            var cache = new SqliteTranslationCache(paths);
            using var coordinator = CreateCoordinator(provider, cache);
            var job = CreateJob();

            var tasks = Enumerable.Range(0, 8).Select(_ => coordinator.TranslateAsync(job)).ToArray();
            await WaitUntilAsync(() => handler.CallCount == 1);
            release.SetResult();
            await Task.WhenAll(tasks);
            var cached = await coordinator.TranslateAsync(job);

            Assert.Equal(1, handler.CallCount);
            Assert.True(cached.IsCacheHit);

            var reopenedCache = new SqliteTranslationCache(paths);
            Assert.NotNull(await reopenedCache.GetAsync(job.CreateCacheKey("openai")));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task Pipeline_Retries429ServerAndOfflineFailuresThenSucceeds()
    {
        var sequence = new ConcurrentQueue<Func<HttpResponseMessage>>([
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.BadGateway),
            () => JsonResponse(SuccessJson)
        ]);
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(sequence.TryDequeue(out var next) ? next() : JsonResponse(SuccessJson)));
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider(
            client,
            "openai",
            "https://api.example.test/v1/",
            "test-secret");
        using var coordinator = CreateCoordinator(provider, new MemoryCache());

        var result = await coordinator.TranslateAsync(CreateJob());

        Assert.Equal("测试译文", result.Translation);
        Assert.Equal(4, handler.CallCount);

        var offlineAttempt = 0;
        var offlineHandler = new DelegateHandler((_, _) =>
        {
            if (Interlocked.Increment(ref offlineAttempt) < 3)
            {
                throw new HttpRequestException("simulated offline");
            }

            return Task.FromResult(JsonResponse(SuccessJson));
        });
        using var offlineClient = new HttpClient(offlineHandler);
        var offlineProvider = new OpenAiCompatibleTranslationProvider(
            offlineClient,
            "openai",
            "https://api.example.test/v1/",
            "test-secret");
        using var offlineCoordinator = CreateCoordinator(offlineProvider, new MemoryCache());

        await offlineCoordinator.TranslateAsync(CreateJob("offline source"));
        Assert.Equal(3, offlineHandler.CallCount);
    }

    [Fact]
    public async Task Pipeline_DoesNotRetry401AndHonorsTimeoutAndCancellation()
    {
        var unauthorizedHandler = new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var unauthorizedClient = new HttpClient(unauthorizedHandler);
        var unauthorizedProvider = new OpenAiCompatibleTranslationProvider(
            unauthorizedClient,
            "openai",
            "https://api.example.test/v1/",
            "test-secret");
        using var unauthorizedCoordinator = CreateCoordinator(unauthorizedProvider, new MemoryCache());

        await Assert.ThrowsAsync<TranslationProviderException>(() =>
            unauthorizedCoordinator.TranslateAsync(CreateJob()));
        Assert.Equal(1, unauthorizedHandler.CallCount);

        var blockingHandler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(SuccessJson);
        });
        using var blockingClient = new HttpClient(blockingHandler);
        var blockingProvider = new OpenAiCompatibleTranslationProvider(
            blockingClient,
            "openai",
            "https://api.example.test/v1/",
            "test-secret");
        using var timeoutCoordinator = new TranslationCoordinator(
            blockingProvider,
            new MemoryCache(),
            new TranslationExecutionOptions(1, TimeSpan.FromMilliseconds(50), RetryDelay: TimeSpan.Zero));

        await Assert.ThrowsAsync<TimeoutException>(() => timeoutCoordinator.TranslateAsync(CreateJob("timeout")));

        using var cancellationCoordinator = CreateCoordinator(blockingProvider, new MemoryCache());
        using var cancellation = new CancellationTokenSource();
        var cancelledTask = cancellationCoordinator.TranslateAsync(CreateJob("cancel"), cancellation.Token);
        await WaitUntilAsync(() => blockingHandler.CallCount >= 2);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);
    }

    [Fact]
    public async Task SettingsAndCacheFilesDoNotContainApiKeyOrSourceText()
    {
        const string apiKey = "sensitive-api-key-value";
        const string sourceText = "private source sentence";
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var settingsStore = new JsonTranslationSettingsStore(paths);
            await settingsStore.SaveAsync(TranslationServiceSettings.Default with
            {
                Model = "configured-model",
                CustomInstruction = "formal style"
            });
            var reloadedSettings = await new JsonTranslationSettingsStore(paths).LoadAsync();
            var cache = new SqliteTranslationCache(paths);
            var job = CreateJob(sourceText);
            await cache.SetAsync(
                job.CreateCacheKey("openai"),
                new CachedTranslation("私密句子", job.Model, null, null, DateTimeOffset.UtcNow));

            var settingsJson = await File.ReadAllTextAsync(paths.TranslationSettingsPath);
            Assert.DoesNotContain(apiKey, settingsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("ApiKey", settingsJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("configured-model", reloadedSettings.Model);
            Assert.Equal("formal style", reloadedSettings.CustomInstruction);

            await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_text_hash, translation FROM translation_cache;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(64, reader.GetString(0).Length);
            Assert.Equal("私密句子", reader.GetString(1));
            Assert.DoesNotContain(sourceText, reader.GetString(0), StringComparison.Ordinal);
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    private const string SuccessJson = """
        {"model":"test-model","choices":[{"message":{"content":"测试译文"}}],"usage":{"prompt_tokens":3,"completion_tokens":4}}
        """;

    private static TranslationRequest CreateRequest() =>
        new("test source", "test-model", string.Empty, new Dictionary<string, string>());

    private static TranslationJob CreateJob(string sourceText = "test source") =>
        new(
            "document-hash",
            sourceText,
            "test-model",
            TranslationGranularity.Selection,
            string.Empty,
            new Dictionary<string, string>(),
            "g1",
            "none");

    private static TranslationCoordinator CreateCoordinator(ITranslationProvider provider, ITranslationCache cache) =>
        new(
            provider,
            cache,
            new TranslationExecutionOptions(2, TimeSpan.FromSeconds(2), RetryDelay: TimeSpan.FromMilliseconds(1)),
            () => 0);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static string CreateIsolatedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperBridge.Tests", $"translation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteIsolatedRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return send(request, cancellationToken);
        }
    }

    private sealed class MemoryCache : ITranslationCache
    {
        private readonly ConcurrentDictionary<string, CachedTranslation> _values = new();

        public Task<CachedTranslation?> GetAsync(
            TranslationCacheKey key,
            CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(key.ToStableId(), out var value);
            return Task.FromResult(value);
        }

        public Task SetAsync(
            TranslationCacheKey key,
            CachedTranslation translation,
            CancellationToken cancellationToken = default)
        {
            _values[key.ToStableId()] = translation;
            return Task.CompletedTask;
        }
    }
}
