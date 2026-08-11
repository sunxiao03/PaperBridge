namespace PaperBridge.Domain.Documents;

public sealed class PaperDocument
{
    public PaperDocument(DocumentId id, string contentHash, string managedFileName, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedFileName);

        Id = id;
        ContentHash = contentHash;
        ManagedFileName = managedFileName;
        Title = string.IsNullOrWhiteSpace(title) ? managedFileName : title.Trim();
    }

    public DocumentId Id { get; }

    public string ContentHash { get; }

    public string ManagedFileName { get; }

    public string Title { get; private set; }

    public string? Authors { get; private set; }

    public int? PublicationYear { get; private set; }

    public string? Journal { get; private set; }

    public string? Doi { get; private set; }

    public void UpdateMetadata(string title, string? authors, int? publicationYear, string? journal, string? doi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Title = title.Trim();
        Authors = Normalize(authors);
        PublicationYear = publicationYear;
        Journal = Normalize(journal);
        Doi = Normalize(doi)?.ToLowerInvariant();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

