using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PaperBridge.Application.Abstractions;

namespace PaperBridge.Infrastructure.Translation;

public sealed class OpenAiCompatibleReadingAssistantProvider : IReadingAssistantProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    public OpenAiCompatibleReadingAssistantProvider(
        HttpClient httpClient,
        string providerId,
        string baseUrl,
        string apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Base URL must be an absolute HTTPS URL.", nameof(baseUrl));
        }

        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        ProviderId = providerId.Trim().ToLowerInvariant();
        _endpoint = new Uri(baseUri, "chat/completions");
        _apiKey = apiKey;
    }

    public string ProviderId { get; }

    public async Task<ReadingAssistantResponse> CompleteAsync(
        ReadingAssistantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Content = JsonContent.Create(new
        {
            model = request.Model,
            temperature = 0,
            max_tokens = request.MaximumOutputTokens,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            }
        });

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            var error = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API 鉴权失败，请检查密钥和服务商设置。",
                HttpStatusCode.TooManyRequests => "AI 服务请求过于频繁。",
                _ when (int)response.StatusCode >= 500 => "AI 服务暂时不可用。",
                _ => $"AI 阅读请求无效（HTTP {(int)response.StatusCode}）。"
            };
            throw new TranslationProviderException(error, response.StatusCode, transient);
        }

        await response.Content.LoadIntoBufferAsync(1_048_576, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var content = root.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new TranslationProviderException("AI 服务返回了空内容。");
            }

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = TryReadInt32(usage, "prompt_tokens");
                outputTokens = TryReadInt32(usage, "completion_tokens");
            }

            var model = root.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString() ?? request.Model
                : request.Model;
            return new ReadingAssistantResponse(content.Trim(), model, inputTokens, outputTokens);
        }
        catch (JsonException exception)
        {
            throw new TranslationProviderException("AI 服务返回了无法解析的响应。", innerException: exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new TranslationProviderException("AI 服务响应缺少必要字段。", innerException: exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new TranslationProviderException("AI 服务响应格式无效。", innerException: exception);
        }
        catch (IndexOutOfRangeException exception)
        {
            throw new TranslationProviderException("AI 服务响应不包含候选内容。", innerException: exception);
        }
    }

    private static int? TryReadInt32(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
}
