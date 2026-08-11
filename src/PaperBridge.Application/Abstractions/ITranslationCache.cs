using PaperBridge.Domain.Translations;

namespace PaperBridge.Application.Abstractions;

public interface ITranslationCache
{
    Task<CachedTranslation?> GetAsync(
        TranslationCacheKey key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        TranslationCacheKey key,
        CachedTranslation translation,
        CancellationToken cancellationToken = default);
}

public sealed record CachedTranslation(
    string Translation,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset CreatedAtUtc);
