using PaperBridge.Application.Reading;

namespace PaperBridge.Application.Abstractions;

public interface IReadingAssistantProvider
{
    string ProviderId { get; }

    Task<ReadingAssistantResponse> CompleteAsync(
        ReadingAssistantRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ReadingAssistantRequest(
    ReadingTaskKind TaskKind,
    string Model,
    string SystemPrompt,
    string UserPrompt,
    int MaximumOutputTokens = 2_000);

public sealed record ReadingAssistantResponse(
    string Content,
    string Model,
    int? InputTokens,
    int? OutputTokens);
