using System.Net;
using PaperBridge.Domain.Translations;

namespace PaperBridge.Application.Abstractions;

public interface ITranslationProvider
{
    string ProviderId { get; }

    Task<TranslationResponse> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TranslationRequest(
    string SourceText,
    string Model,
    string Context,
    IReadOnlyDictionary<string, string> Terminology,
    string? CustomInstruction = null,
    TranslationGranularity Granularity = TranslationGranularity.Selection,
    string SourceLanguage = "en",
    string TargetLanguage = "zh-CN");

public sealed record TranslationResponse(string Translation, string Model, int? InputTokens, int? OutputTokens);

public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(
        string message,
        HttpStatusCode? statusCode = null,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
    }

    public HttpStatusCode? StatusCode { get; }

    public bool IsTransient { get; }
}
