using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PaperBridge.Application.Abstractions;

namespace PaperBridge.Infrastructure.Translation;

public sealed class OpenAiCompatibleTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    public OpenAiCompatibleTranslationProvider(
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

    public async Task<TranslationResponse> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Content = JsonContent.Create(new
        {
            model = request.Model,
            temperature = 0,
            messages = new[]
            {
                new { role = "system", content = BuildSystemPrompt(request) },
                new { role = "user", content = BuildUserPrompt(request) }
            }
        });

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateStatusException(response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var translation = root.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(translation))
            {
                throw new TranslationProviderException("翻译服务返回了空内容。");
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
            return new TranslationResponse(translation.Trim(), model, inputTokens, outputTokens);
        }
        catch (JsonException exception)
        {
            throw new TranslationProviderException("翻译服务返回了无法解析的响应。", innerException: exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new TranslationProviderException("翻译服务响应缺少必要字段。", innerException: exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new TranslationProviderException("翻译服务响应格式无效。", innerException: exception);
        }
        catch (IndexOutOfRangeException exception)
        {
            throw new TranslationProviderException("翻译服务响应不包含候选译文。", innerException: exception);
        }
    }

    private static TranslationProviderException CreateStatusException(HttpStatusCode statusCode)
    {
        var transient = statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API 鉴权失败，请检查密钥和服务商设置。",
            HttpStatusCode.TooManyRequests => "翻译服务请求过于频繁。",
            _ when (int)statusCode >= 500 => "翻译服务暂时不可用。",
            _ => $"翻译请求无效（HTTP {(int)statusCode}）。"
        };
        return new TranslationProviderException(message, statusCode, transient);
    }

    private static string BuildSystemPrompt(TranslationRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You translate English scientific literature into accurate, natural Simplified Chinese.");
        builder.AppendLine("Preserve equations, mathematical symbols, variable names, citations, units, and identifiers exactly; do not translate formulas.");
        builder.AppendLine("Return only the Chinese translation. Do not add commentary or Markdown fences.");
        builder.AppendLine($"Translation granularity: {request.Granularity}.");
        if (request.Terminology.Count > 0)
        {
            builder.AppendLine("Required terminology:");
            foreach (var term in request.Terminology.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {term.Key} => {term.Value}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.CustomInstruction))
        {
            builder.AppendLine("Additional user instruction:");
            builder.AppendLine(request.CustomInstruction.Trim());
        }

        return builder.ToString();
    }

    private static string BuildUserPrompt(TranslationRequest request)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            builder.AppendLine("Context (use only for disambiguation):");
            builder.AppendLine(request.Context.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("Text to translate:");
        builder.Append(request.SourceText);
        return builder.ToString();
    }

    private static int? TryReadInt32(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
}
