namespace PaperBridge.Application.Bilingual;

public enum BilingualLayoutMode
{
    Paragraph = 0,
    PageAligned = 1
}

public sealed record SourceParagraph(
    string SegmentId,
    int PageIndex,
    int SourceStart,
    int SourceLength,
    string Text,
    double? Left,
    double? Top,
    double? Width,
    double? Height);

public sealed record PageLayoutAnalysis(
    int PageIndex,
    BilingualLayoutMode RecommendedMode,
    double Confidence,
    string? DegradationReason,
    IReadOnlyList<SourceParagraph> Paragraphs,
    bool HasMultipleColumns,
    double FormulaDensity,
    bool MayCrossPage);

public sealed record StoredBilingualSegment(
    string DocumentHash,
    int PageIndex,
    string SegmentId,
    int SourceStart,
    int SourceLength,
    string SourceText,
    string SourceTextHash,
    string MachineTranslation,
    string? UserTranslation,
    string Provider,
    string Model,
    string PromptVersion,
    string GlossaryVersion,
    BilingualLayoutMode LayoutMode,
    double LayoutConfidence,
    string? DegradationReason,
    DateTimeOffset MachineUpdatedAtUtc,
    DateTimeOffset? UserUpdatedAtUtc)
{
    public string DisplayTranslation => string.IsNullOrWhiteSpace(UserTranslation)
        ? MachineTranslation
        : UserTranslation;

    public bool HasUserTranslation => !string.IsNullOrWhiteSpace(UserTranslation);
}
