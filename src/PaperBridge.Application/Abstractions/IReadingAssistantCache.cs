using PaperBridge.Application.Reading;

namespace PaperBridge.Application.Abstractions;

public interface IReadingAssistantCache
{
    Task<CachedReadingAssistantResult?> GetAsync(
        ReadingAssistantCacheKey key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        ReadingAssistantCacheKey key,
        CachedReadingAssistantResult result,
        CancellationToken cancellationToken = default);
}

public sealed record CachedReadingAssistantResult(
    string Content,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset CreatedAtUtc);
