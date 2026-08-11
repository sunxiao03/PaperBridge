using Microsoft.Data.Sqlite;
using PaperBridge.Application.Annotations;
using PaperBridge.Infrastructure.Pdf;
using PaperBridge.Infrastructure.Storage;

namespace PaperBridge.Infrastructure.Tests.Storage;

public sealed class SqliteAnnotationStoreTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "pdfium-text-layer-sample.pdf");

    [Fact]
    public async Task AnnotationRoundTripPersistsCoordinatesTranslationAndStatus()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var imported = await library.ImportPdfAsync(FixturePath);
            var annotation = CreateAnnotation(imported.Document, AnnotationKind.Highlight, 0);
            var store = new SqliteAnnotationStore(paths);

            await store.SaveAsync(annotation);
            var saved = Assert.Single(await store.GetForDocumentAsync(imported.Document.Id));

            Assert.Equal(annotation.SelectedText, saved.SelectedText);
            Assert.Equal(annotation.Rectangles, saved.Rectangles);
            Assert.Equal("关联译文", saved.LinkedTranslation);
            Assert.Equal(AnnotationAnchorStatus.Valid, saved.AnchorStatus);

            await store.SaveAsync(saved with
            {
                NoteText = "updated",
                AnchorStatus = AnnotationAnchorStatus.Migrated,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            saved = Assert.Single(await store.GetForDocumentAsync(imported.Document.Id));
            Assert.Equal("updated", saved.NoteText);
            Assert.Equal(AnnotationAnchorStatus.Migrated, saved.AnchorStatus);

            await store.DeleteAsync(saved.Id);
            Assert.Empty(await store.GetForDocumentAsync(imported.Document.Id));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SavingSecondBookmarkReplacesBookmarkOnSamePage()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var imported = await library.ImportPdfAsync(FixturePath);
            var store = new SqliteAnnotationStore(paths);
            var first = AnnotationAnchorService.CreateBookmark(
                imported.Document.Id, imported.Document.ContentHash, 1, "#FF6B6B", "first");
            var second = AnnotationAnchorService.CreateBookmark(
                imported.Document.Id, imported.Document.ContentHash, 1, "#FF6B6B", "second");

            await store.SaveAsync(first);
            await store.SaveAsync(second);
            var bookmark = Assert.Single(await store.GetForDocumentAsync(imported.Document.Id));

            Assert.Equal(second.Id, bookmark.Id);
            Assert.Equal("second", bookmark.NoteText);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task VersionSixMigrationCreatesAnnotationSchema()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            await new ManagedDocumentLibrary(paths).InitializeAsync();
            await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('document_annotations');";
            Assert.Equal(18L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(7L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task PdfTextSelectionProducesNormalizedPersistentRectangles()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var imported = await library.ImportPdfAsync(FixturePath);
            await using var pdf = PdfiumDocument.Open(library.GetManagedFilePath(imported.Document));
            var page = await pdf.ExtractPageTextAsync(0);
            const string selectedText = "Neutron flux remains stable";
            var start = page.Text.IndexOf(selectedText, StringComparison.Ordinal);
            Assert.True(start >= 0);

            var annotation = AnnotationAnchorService.CreateTextAnnotation(
                imported.Document.Id,
                imported.Document.ContentHash,
                page,
                start,
                selectedText.Length,
                AnnotationKind.Highlight,
                "#FFE066");
            var store = new SqliteAnnotationStore(paths);
            await store.SaveAsync(annotation);

            var saved = Assert.Single(await store.GetForDocumentAsync(imported.Document.Id));
            Assert.NotEmpty(saved.Rectangles);
            Assert.All(saved.Rectangles, rectangle =>
            {
                Assert.InRange(rectangle.Left, 0, 1);
                Assert.InRange(rectangle.Top, 0, 1);
                Assert.InRange(rectangle.Left + rectangle.Width, 0, 1.001);
                Assert.InRange(rectangle.Top + rectangle.Height, 0, 1.001);
            });
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RemovingDocumentCascadesToAnnotations()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            var imported = await library.ImportPdfAsync(FixturePath);
            var store = new SqliteAnnotationStore(paths);
            await store.SaveAsync(CreateAnnotation(imported.Document, AnnotationKind.Note, 0));

            await library.RemoveDocumentAsync(imported.Document.Id, deleteManagedFile: false);

            Assert.Empty(await store.GetForDocumentAsync(imported.Document.Id));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static DocumentAnnotation CreateAnnotation(
        PaperBridge.Domain.Documents.LibraryDocument document,
        AnnotationKind kind,
        int pageIndex)
    {
        var now = DateTimeOffset.UtcNow;
        return new DocumentAnnotation(
            Guid.NewGuid(), document.Id, document.ContentHash, pageIndex, kind,
            "neutron flux", 4, 12, AnnotationAnchorService.Fingerprint("neutron flux"),
            "the", "is stable", [new NormalizedPdfRectangle(0.1, 0.2, 0.3, 0.02)],
            "note", "关联译文", "#FFE066", AnnotationAnchorStatus.Valid, now, now);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperBridgeAnnotationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
