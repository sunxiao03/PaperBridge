using System.Net;
using System.Text;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Reading;
using PaperBridge.Infrastructure.Translation;

namespace PaperBridge.Infrastructure.Tests.Translation;

public sealed class ReadingAssistantProviderTests
{
    [Fact]
    public async Task CompleteAsyncUsesBoundedChatRequestAndParsesUsage()
    {
        string? requestBody = null;
        string? authorization = null;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            authorization = request.Headers.Authorization?.ToString();
            return JsonResponse("""
                {"model":"returned-model","choices":[{"message":{"content":"有据回答 [E1]。"}}],"usage":{"prompt_tokens":42,"completion_tokens":9}}
                """);
        });
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiCompatibleReadingAssistantProvider(
            httpClient, "openai", "https://example.invalid/v1", "unit-test-sensitive-value");

        var result = await provider.CompleteAsync(new ReadingAssistantRequest(
            ReadingTaskKind.QuestionAnswer,
            "requested-model",
            "system rules",
            "user evidence",
            777));

        Assert.Equal("有据回答 [E1]。", result.Content);
        Assert.Equal("returned-model", result.Model);
        Assert.Equal(42, result.InputTokens);
        Assert.Equal(9, result.OutputTokens);
        Assert.Equal("Bearer unit-test-sensitive-value", authorization);
        Assert.Contains("\"max_tokens\":777", requestBody, StringComparison.Ordinal);
        Assert.Contains("system rules", requestBody, StringComparison.Ordinal);
        Assert.Contains("user evidence", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsyncClassifiesRateLimitWithoutLeakingResponseBody()
    {
        const string sensitiveBody = "server-secret-that-must-not-appear";
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(sensitiveBody)
        }));
        using var httpClient = new HttpClient(handler);
        var provider = new OpenAiCompatibleReadingAssistantProvider(
            httpClient, "openai", "https://example.invalid/v1", "unit-test-sensitive-value");

        var exception = await Assert.ThrowsAsync<TranslationProviderException>(() => provider.CompleteAsync(
            new ReadingAssistantRequest(ReadingTaskKind.QueryExpansion, "model", "system", "user")));

        Assert.True(exception.IsTransient);
        Assert.DoesNotContain(sensitiveBody, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test-sensitive-value", exception.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
