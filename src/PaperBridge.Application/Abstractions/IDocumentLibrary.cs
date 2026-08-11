using PaperBridge.Domain.Documents;

namespace PaperBridge.Application.Abstractions;

public interface IDocumentLibrary
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryDocument>> GetDocumentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryDocument>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<DocumentImportResult> ImportPdfAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);

    Task<LibraryFolder> CreateFolderAsync(string name, CancellationToken cancellationToken = default);

    Task RenameFolderAsync(Guid folderId, string name, CancellationToken cancellationToken = default);

    Task DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken = default);

    Task SetDocumentFolderAsync(
        DocumentId documentId,
        Guid? folderId,
        CancellationToken cancellationToken = default);

    Task SetDocumentTagsAsync(
        DocumentId documentId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(
        DocumentId documentId,
        bool isFavorite,
        CancellationToken cancellationToken = default);

    Task RemoveDocumentAsync(
        DocumentId documentId,
        bool deleteManagedFile,
        CancellationToken cancellationToken = default);

    Task UpdateReadingPositionAsync(
        DocumentId documentId,
        int pageIndex,
        double scrollOffset,
        CancellationToken cancellationToken = default);

    string GetManagedFilePath(LibraryDocument document);
}

public sealed record DocumentImportResult(
    LibraryDocument Document,
    bool WasImported,
    bool WasDuplicate);
