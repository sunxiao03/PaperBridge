using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Annotations;
using PaperBridge.Domain.Documents;

namespace PaperBridge.Domain.Tests.Annotations;

public sealed class AnnotationAnchorServiceTests
{
    [Fact]
    public void CreateTextAnnotationBuildsScaleIndependentRectanglesAndFingerprint()
    {
        var page = CreatePage("The neutron flux is stable.");

        var annotation = AnnotationAnchorService.CreateTextAnnotation(
            new DocumentId(Guid.NewGuid()),
            new string('a', 64),
            page,
            4,
            12,
            AnnotationKind.Highlight,
            "#FFE066",
            "important",
            "中子通量");

        Assert.Equal("neutron flux", annotation.SelectedText);
        Assert.Equal(64, annotation.TextFingerprint?.Length);
        Assert.NotEmpty(annotation.Rectangles);
        Assert.All(annotation.Rectangles, rectangle =>
        {
            Assert.InRange(rectangle.Left, 0, 1);
            Assert.InRange(rectangle.Top, 0, 1);
            Assert.InRange(rectangle.Left + rectangle.Width, 0, 1.001);
        });
        Assert.Equal("中子通量", annotation.LinkedTranslation);
    }

    [Fact]
    public void ResolveMigratesOnlyWhenSelectedTextHasUniqueMatch()
    {
        var original = CreatePage("Prefix. neutron flux remains stable.");
        var annotation = AnnotationAnchorService.CreateTextAnnotation(
            new DocumentId(Guid.NewGuid()), new string('b', 64), original, 8, 12,
            AnnotationKind.Underline, "#4D96FF");
        var shifted = CreatePage("A new heading. Prefix. neutron flux remains stable.");

        var result = AnnotationAnchorService.Resolve(annotation, annotation.DocumentHash, shifted);

        Assert.Equal(AnnotationAnchorStatus.Migrated, result.Annotation.AnchorStatus);
        Assert.True(result.WasChanged);
        Assert.Equal(23, result.Annotation.SourceStart);
        Assert.Contains("安全迁移", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePreservesMigratedStatusAfterReopenWithoutRewritingTimestamp()
    {
        var original = CreatePage("Prefix. neutron flux remains stable.");
        var annotation = AnnotationAnchorService.CreateTextAnnotation(
            new DocumentId(Guid.NewGuid()), new string('b', 64), original, 8, 12,
            AnnotationKind.Underline, "#4D96FF");
        var shifted = CreatePage("A new heading. Prefix. neutron flux remains stable.");
        var migrated = AnnotationAnchorService.Resolve(annotation, annotation.DocumentHash, shifted).Annotation;

        var reopened = AnnotationAnchorService.Resolve(migrated, annotation.DocumentHash, shifted);

        Assert.Equal(AnnotationAnchorStatus.Migrated, reopened.Annotation.AnchorStatus);
        Assert.False(reopened.WasChanged);
        Assert.Equal(migrated.UpdatedAtUtc, reopened.Annotation.UpdatedAtUtc);
    }

    [Fact]
    public void ResolveMarksAmbiguousAndChangedDocumentAnchorsExplicitly()
    {
        var page = CreatePage("neutron flux is stable.");
        var annotation = AnnotationAnchorService.CreateTextAnnotation(
            new DocumentId(Guid.NewGuid()), new string('c', 64), page, 0, 12,
            AnnotationKind.Note, "#FF922B");
        var ambiguous = CreatePage("heading neutron flux and neutron flux.");

        var orphaned = AnnotationAnchorService.Resolve(annotation, annotation.DocumentHash, ambiguous);
        var changed = AnnotationAnchorService.Resolve(annotation, new string('d', 64), page);

        Assert.Equal(AnnotationAnchorStatus.Orphaned, orphaned.Annotation.AnchorStatus);
        Assert.Contains("多次", orphaned.Message, StringComparison.Ordinal);
        Assert.Equal(AnnotationAnchorStatus.DocumentChanged, changed.Annotation.AnchorStatus);
        Assert.Contains("哈希", changed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarkDoesNotRequireTextCoordinates()
    {
        var bookmark = AnnotationAnchorService.CreateBookmark(
            new DocumentId(Guid.NewGuid()), new string('e', 64), 7, "#FF6B6B", "chapter");

        var resolved = AnnotationAnchorService.Resolve(bookmark, bookmark.DocumentHash, null);

        Assert.Equal(AnnotationKind.Bookmark, bookmark.Kind);
        Assert.Empty(bookmark.Rectangles);
        Assert.Equal(AnnotationAnchorStatus.Valid, resolved.Annotation.AnchorStatus);
    }

    private static PdfPageText CreatePage(string text)
    {
        var characters = text.Select((character, index) => new PdfTextCharacter(
            index,
            character.ToString(),
            character == ' '
                ? null
                : new PdfRectangle(40 + (index * 6), 700, 45 + (index * 6), 712))).ToArray();
        return new PdfPageText(0, 612, 792, text, characters, []);
    }
}
