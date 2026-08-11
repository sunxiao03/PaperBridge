using System.Security.Cryptography;
using PaperBridge.Application.Abstractions;
using PaperBridge.Domain.Documents;
using PaperBridge.Infrastructure.Pdf;

namespace PaperBridge.Infrastructure.Storage;

public sealed class ManagedDocumentLibrary : IDocumentLibrary
{
    private readonly AppDataPaths _paths;
    private readonly SqliteDocumentRepository _repository;
    private readonly SqliteDatabaseBackupService _backupService;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public ManagedDocumentLibrary(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _repository = new SqliteDocumentRepository(paths.DatabasePath);
        _backupService = new SqliteDatabaseBackupService(paths);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectoriesExist();
        await _repository.InitializeAsync(cancellationToken);
        await _backupService.CreateDailyBackupIfDueAsync(cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<LibraryDocument>> GetDocumentsAsync(CancellationToken cancellationToken = default) =>
        _repository.GetAllAsync(cancellationToken);

    public Task<IReadOnlyList<LibraryDocument>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetDocumentsAsync(cancellationToken);
        }

        var ftsQuery = BuildFtsQuery(query);
        return string.IsNullOrEmpty(ftsQuery)
            ? GetDocumentsAsync(cancellationToken)
            : _repository.SearchAsync(ftsQuery, cancellationToken);
    }

    public async Task<DocumentImportResult> ImportPdfAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken);
        try
        {
            return await ImportPdfCoreAsync(sourceFilePath, cancellationToken);
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<DocumentImportResult> ImportPdfCoreAsync(
        string sourceFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        var fullSourcePath = Path.GetFullPath(sourceFilePath);

        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("The PDF file was not found.", fullSourcePath);
        }

        if (!string.Equals(Path.GetExtension(fullSourcePath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only PDF files can be imported.");
        }

        DetectedPdfMetadata detectedMetadata;
        await using (var validationDocument = PdfiumDocument.Open(fullSourcePath))
        {
            if (validationDocument.PageCount <= 0)
            {
                throw new InvalidDataException("The PDF does not contain any pages.");
            }

            var embeddedMetadata = await validationDocument.GetMetadataAsync(cancellationToken);
            var firstPage = await validationDocument.ExtractPageTextAsync(0, cancellationToken);
            detectedMetadata = LocalPdfMetadataDetector.Detect(
                embeddedMetadata,
                firstPage.Text,
                Path.GetFileNameWithoutExtension(fullSourcePath));
        }

        var contentHash = await ComputeSha256Async(fullSourcePath, cancellationToken);
        var existing = await _repository.FindByContentHashAsync(contentHash, cancellationToken);
        if (existing is not null)
        {
            return new DocumentImportResult(existing, WasImported: false, WasDuplicate: true);
        }

        var managedFileName = $"{contentHash}.pdf";
        var managedPath = Path.Combine(_paths.LibraryDirectory, managedFileName);
        var temporaryPath = Path.Combine(_paths.LibraryDirectory, $".import-{Guid.NewGuid():N}.tmp");
        var createdManagedFile = false;

        try
        {
            await CopyFileAsync(fullSourcePath, temporaryPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(managedPath))
            {
                File.Delete(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, managedPath);
                createdManagedFile = true;
            }

            var document = new LibraryDocument(
                DocumentId.New(),
                contentHash,
                managedFileName,
                detectedMetadata.Title,
                detectedMetadata.Authors,
                detectedMetadata.PublicationYear,
                detectedMetadata.Journal,
                detectedMetadata.Doi,
                DateTimeOffset.UtcNow,
                LastOpenedAtUtc: null,
                LastPageIndex: 0,
                LastScrollOffset: 0,
                IsFavorite: false);

            if (await _repository.InsertAsync(document, cancellationToken))
            {
                return new DocumentImportResult(document, WasImported: true, WasDuplicate: false);
            }

            existing = await _repository.FindByContentHashAsync(contentHash, cancellationToken)
                ?? throw new InvalidOperationException("The document import conflicted, but the existing record was not found.");
            return new DocumentImportResult(existing, WasImported: false, WasDuplicate: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            if (createdManagedFile)
            {
                TryDeleteTemporaryFile(managedPath);
            }

            throw;
        }
    }

    public Task UpdateReadingPositionAsync(
        DocumentId documentId,
        int pageIndex,
        double scrollOffset,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (!double.IsFinite(scrollOffset) || scrollOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scrollOffset));
        }

        return _repository.UpdateReadingPositionAsync(
            documentId,
            pageIndex,
            scrollOffset,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        _repository.GetFoldersAsync(cancellationToken);

    public async Task<LibraryFolder> CreateFolderAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeFolderName(name);
        try
        {
            return await _repository.CreateFolderAsync(normalizedName, cancellationToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"分类“{normalizedName}”已经存在。", exception);
        }
    }

    public async Task RenameFolderAsync(
        Guid folderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeFolderName(name);
        try
        {
            await _repository.RenameFolderAsync(folderId, normalizedName, cancellationToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"分类“{normalizedName}”已经存在。", exception);
        }
    }

    public Task DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken = default) =>
        _repository.DeleteFolderAsync(folderId, cancellationToken);

    public Task SetDocumentFolderAsync(
        DocumentId documentId,
        Guid? folderId,
        CancellationToken cancellationToken = default) =>
        _repository.SetDocumentFolderAsync(documentId, folderId, cancellationToken);

    public Task SetDocumentTagsAsync(
        DocumentId documentId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var normalizedTags = tags
            .Select(NormalizeTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTags.Length > 32)
        {
            throw new ArgumentException("每篇文献最多使用 32 个标签。", nameof(tags));
        }

        return _repository.SetDocumentTagsAsync(documentId, normalizedTags, cancellationToken);
    }

    public Task SetFavoriteAsync(
        DocumentId documentId,
        bool isFavorite,
        CancellationToken cancellationToken = default) =>
        _repository.SetFavoriteAsync(documentId, isFavorite, cancellationToken);

    public async Task RemoveDocumentAsync(
        DocumentId documentId,
        bool deleteManagedFile,
        CancellationToken cancellationToken = default)
    {
        var document = await _repository.FindByIdAsync(documentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Document '{documentId}' was not found.");
        if (!deleteManagedFile)
        {
            await _repository.RemoveDocumentAsync(documentId, cancellationToken);
            return;
        }

        var managedPath = GetManagedFilePath(document);
        if (!File.Exists(managedPath))
        {
            throw new FileNotFoundException("受管理 PDF 副本不存在，未删除文献记录。", managedPath);
        }

        var quarantinePath = Path.Combine(
            _paths.LibraryDirectory,
            $".delete-{Guid.NewGuid():N}.tmp");
        File.Move(managedPath, quarantinePath);
        try
        {
            await _repository.RemoveDocumentAsync(documentId, cancellationToken);
        }
        catch
        {
            File.Move(quarantinePath, managedPath);
            throw;
        }

        try
        {
            File.Delete(quarantinePath);
        }
        catch
        {
            File.Move(quarantinePath, managedPath);
            throw;
        }
    }

    public string GetManagedFilePath(LibraryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.ManagedFileName,
                Path.GetFileName(document.ManagedFileName),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The managed document file name is invalid.");
        }

        return Path.Combine(_paths.LibraryDirectory, document.ManagedFileName);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(
            " AND ",
            terms.Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*"));
    }

    private static string NormalizeFolderName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 80 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("分类名称必须为 1–80 个可显示字符。", nameof(name));
        }

        return normalized;
    }

    private static string NormalizeTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var normalized = tag.Trim();
        if (normalized.Length > 40 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("标签必须为 1–40 个可显示字符。", nameof(tag));
        }

        return normalized;
    }

    private static void TryDeleteTemporaryFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
