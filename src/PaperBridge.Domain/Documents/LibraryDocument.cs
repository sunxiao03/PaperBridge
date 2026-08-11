namespace PaperBridge.Domain.Documents;

public sealed record LibraryDocument(
    DocumentId Id,
    string ContentHash,
    string ManagedFileName,
    string Title,
    string? Authors,
    int? PublicationYear,
    string? Journal,
    string? Doi,
    DateTimeOffset ImportedAtUtc,
    DateTimeOffset? LastOpenedAtUtc,
    int LastPageIndex,
    double LastScrollOffset,
    bool IsFavorite,
    Guid? FolderId = null,
    string? FolderName = null,
    IReadOnlyList<string>? Tags = null)
{
    public string TagSummary => Tags is { Count: > 0 }
        ? string.Join(" · ", Tags)
        : string.Empty;

    public string ClassificationSummary => string.Join(
        "  ",
        new[] { FolderName, TagSummary }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
