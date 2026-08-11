using System.Security.Cryptography;
using System.Text;

namespace PaperBridge.Application.Reading;

public enum ReadingTaskKind
{
    ExplainSelection = 0,
    TranslateAndExplain = 1,
    SectionSummary = 2,
    DocumentChunkSummary = 3,
    DocumentSynthesis = 4,
    QueryExpansion = 5,
    QuestionAnswer = 6
}

public sealed record ReadingAssistantCacheKey(
    string DocumentHash,
    ReadingTaskKind TaskKind,
    string InputHash,
    string Provider,
    string Model,
    string PromptVersion,
    string CustomInstructionVersion)
{
    public string ToStableId()
    {
        var canonical = string.Join('\n',
            DocumentHash,
            ((int)TaskKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            InputHash,
            Provider.Trim().ToLowerInvariant(),
            Model.Trim(),
            PromptVersion.Trim(),
            CustomInstructionVersion.Trim());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string HashInput(string systemPrompt, string userPrompt)
    {
        var value = systemPrompt.Trim() + "\n\u001f\n" + userPrompt.Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed record ReadingAssistantJob(
    string DocumentHash,
    ReadingTaskKind TaskKind,
    string Model,
    string SystemPrompt,
    string UserPrompt,
    string CustomInstructionVersion,
    bool Cacheable = true,
    int MaximumOutputTokens = 2_000)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DocumentHash) || DocumentHash.Length != 64 ||
            !DocumentHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Document hash must be a SHA-256 hexadecimal value.", nameof(DocumentHash));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(CustomInstructionVersion);
        if (SystemPrompt.Length > 16_000)
        {
            throw new ArgumentException("System prompt exceeds 16,000 characters.", nameof(SystemPrompt));
        }

        if (UserPrompt.Length > 60_000)
        {
            throw new ArgumentException("User prompt exceeds 60,000 characters.", nameof(UserPrompt));
        }

        if (MaximumOutputTokens is < 64 or > 8_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputTokens));
        }
    }
}

public sealed record ReadingAssistantResult(
    string Content,
    string Model,
    int? InputTokens,
    int? OutputTokens,
    bool IsCacheHit);

public sealed record DocumentCorpus(
    int PageCount,
    IReadOnlyList<DocumentSection> Sections,
    IReadOnlyList<DocumentTextChunk> Chunks,
    int TotalCharacters);

public sealed record DocumentSection(
    string Title,
    int StartPageIndex,
    int EndPageIndexExclusive);

public sealed record DocumentTextChunk(
    string StableId,
    int PageIndex,
    string SectionTitle,
    int SourceStart,
    string Text);

public sealed record EvidenceCandidate(
    string CitationId,
    string StableChunkId,
    int PageIndex,
    string SectionTitle,
    string EnglishExcerpt,
    double Score);

public sealed record CitationValidationResult(
    bool IsValid,
    string Message,
    IReadOnlyList<EvidenceCandidate> CitedEvidence);

public sealed record ReadingConversationMessage(bool IsUser, string Text);
