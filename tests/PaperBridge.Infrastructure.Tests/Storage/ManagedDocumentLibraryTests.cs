using Microsoft.Data.Sqlite;
using PaperBridge.Infrastructure.Pdf;
using PaperBridge.Infrastructure.Storage;

namespace PaperBridge.Infrastructure.Tests.Storage;

public sealed class ManagedDocumentLibraryTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "pdfium-text-layer-sample.pdf");

    [Fact]
    public async Task ImportPdfAsync_CopiesIntoManagedLibraryAndDeduplicatesByContent()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();

            var first = await library.ImportPdfAsync(FixturePath);
            var second = await library.ImportPdfAsync(FixturePath);
            var documents = await library.GetDocumentsAsync();

            Assert.True(first.WasImported);
            Assert.False(first.WasDuplicate);
            Assert.False(second.WasImported);
            Assert.True(second.WasDuplicate);
            Assert.Equal(first.Document.Id, second.Document.Id);
            Assert.Single(documents);
            Assert.Equal(64, first.Document.ContentHash.Length);
            Assert.Equal("PDFium Text Layer Fixture", first.Document.Title);
            Assert.Equal("PaperBridge contributors", first.Document.Authors);
            Assert.Equal(2026, first.Document.PublicationYear);
            Assert.Equal("10.1234/paperbridge.fixture", first.Document.Doi);
            Assert.True(File.Exists(library.GetManagedFilePath(first.Document)));
            Assert.True(File.Exists(FixturePath));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task SearchAndReadingPosition_PersistAcrossLibraryInstances()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var firstLibrary = new ManagedDocumentLibrary(paths);
            await firstLibrary.InitializeAsync();
            var imported = await firstLibrary.ImportPdfAsync(FixturePath);

            var searchResults = await firstLibrary.SearchAsync("pdfium text");
            await firstLibrary.UpdateReadingPositionAsync(imported.Document.Id, pageIndex: 1, scrollOffset: 42.5);

            var reopenedLibrary = new ManagedDocumentLibrary(paths);
            await reopenedLibrary.InitializeAsync();
            var reopened = Assert.Single(await reopenedLibrary.GetDocumentsAsync());

            Assert.Single(searchResults);
            Assert.Equal(imported.Document.Id, searchResults[0].Id);
            Assert.Equal(1, reopened.LastPageIndex);
            Assert.Equal(42.5, reopened.LastScrollOffset);
            Assert.NotNull(reopened.LastOpenedAtUtc);
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task ImportPdfAsync_RejectsNonPdfWithoutCreatingLibraryRecord()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var textFile = Path.Combine(root, "not-a-pdf.txt");
            await File.WriteAllTextAsync(textFile, "not a PDF");

            await Assert.ThrowsAsync<InvalidDataException>(() => library.ImportPdfAsync(textFile));

            Assert.Empty(await library.GetDocumentsAsync());
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task ImportPdfAsync_RejectsInvalidPdfWithoutCreatingManagedCopy()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var invalidPdf = Path.Combine(root, "invalid.pdf");
            await File.WriteAllTextAsync(invalidPdf, "%PDF-this-is-not-a-valid-document");

            await Assert.ThrowsAsync<PdfiumException>(() => library.ImportPdfAsync(invalidPdf));

            Assert.Empty(await library.GetDocumentsAsync());
            Assert.Empty(Directory.GetFiles(paths.LibraryDirectory));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task Classification_PersistsFolderTagsAndFavoriteAcrossInstances()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var imported = await library.ImportPdfAsync(FixturePath);
            var folder = await library.CreateFolderAsync("反应堆物理");

            await library.SetDocumentFolderAsync(imported.Document.Id, folder.Id);
            await library.SetDocumentTagsAsync(
                imported.Document.Id,
                ["Transport", "Benchmark", "transport"]);
            await library.SetFavoriteAsync(imported.Document.Id, isFavorite: true);

            var reopenedLibrary = new ManagedDocumentLibrary(paths);
            await reopenedLibrary.InitializeAsync();
            var reopened = Assert.Single(await reopenedLibrary.GetDocumentsAsync());

            Assert.True(reopened.IsFavorite);
            Assert.Equal(folder.Id, reopened.FolderId);
            Assert.Equal("反应堆物理", reopened.FolderName);
            Assert.Equal(["Benchmark", "Transport"], reopened.Tags);

            await reopenedLibrary.RenameFolderAsync(folder.Id, "中子输运");
            reopened = Assert.Single(await reopenedLibrary.GetDocumentsAsync());
            Assert.Equal("中子输运", reopened.FolderName);

            await reopenedLibrary.DeleteFolderAsync(folder.Id);
            reopened = Assert.Single(await reopenedLibrary.GetDocumentsAsync());
            Assert.Null(reopened.FolderId);
            Assert.Null(reopened.FolderName);
            Assert.Equal(["Benchmark", "Transport"], reopened.Tags);
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesExistingVersionOneDatabaseWithoutLosingDocuments()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureDirectoriesExist();
            await CreateVersionOneDatabaseAsync(paths.DatabasePath);

            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var document = Assert.Single(await library.GetDocumentsAsync());
            var folder = await library.CreateFolderAsync("迁移后分类");
            await library.SetDocumentFolderAsync(document.Id, folder.Id);

            document = Assert.Single(await library.GetDocumentsAsync());
            Assert.Equal("保留的文献", document.Title);
            Assert.Equal("迁移后分类", document.FolderName);

            await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(7L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Fact]
    public async Task Classification_RejectsDuplicateFoldersAndInvalidTags()
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var imported = await library.ImportPdfAsync(FixturePath);
            await library.CreateFolderAsync("My Papers");

            await Assert.ThrowsAsync<InvalidOperationException>(() => library.CreateFolderAsync("my papers"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                library.SetDocumentTagsAsync(imported.Document.Id, [new string('x', 41)]));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveDocumentAsync_RespectsManagedFileChoice(bool deleteManagedFile)
    {
        var root = CreateIsolatedRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var imported = await library.ImportPdfAsync(FixturePath);
            var managedPath = library.GetManagedFilePath(imported.Document);

            await library.RemoveDocumentAsync(imported.Document.Id, deleteManagedFile);

            Assert.Empty(await library.GetDocumentsAsync());
            Assert.Equal(!deleteManagedFile, File.Exists(managedPath));
            Assert.True(File.Exists(FixturePath));
        }
        finally
        {
            DeleteIsolatedRoot(root);
        }
    }

    private static async Task CreateVersionOneDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE documents (
                id TEXT NOT NULL PRIMARY KEY,
                content_hash TEXT NOT NULL UNIQUE,
                managed_file_name TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                authors TEXT NULL,
                publication_year INTEGER NULL,
                journal TEXT NULL,
                doi TEXT NULL,
                imported_at_utc TEXT NOT NULL,
                last_opened_at_utc TEXT NULL,
                last_page_index INTEGER NOT NULL DEFAULT 0,
                last_scroll_offset REAL NOT NULL DEFAULT 0,
                is_favorite INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO documents (
                id, content_hash, managed_file_name, title, imported_at_utc)
            VALUES (
                $id, $hash, 'retained.pdf', '保留的文献', '2026-08-10T00:00:00.0000000+00:00');
            PRAGMA user_version = 1;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$hash", new string('a', 64));
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateIsolatedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperBridge.Tests", $"library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteIsolatedRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
