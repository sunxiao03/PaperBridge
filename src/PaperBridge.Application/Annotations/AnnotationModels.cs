using PaperBridge.Domain.Documents;

namespace PaperBridge.Application.Annotations;

public enum AnnotationKind
{
    Highlight = 0,
    Underline = 1,
    Note = 2,
    Bookmark = 3
}

public enum AnnotationAnchorStatus
{
    Valid = 0,
    Migrated = 1,
    Orphaned = 2,
    DocumentChanged = 3
}

public sealed record NormalizedPdfRectangle(double Left, double Top, double Width, double Height)
{
    public NormalizedPdfRectangle Validate()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            Left < 0 || Top < 0 || Width <= 0 || Height <= 0 || Left + Width > 1.001 || Top + Height > 1.001)
        {
            throw new ArgumentOutOfRangeException(nameof(NormalizedPdfRectangle));
        }

        return this;
    }
}

public sealed record DocumentAnnotation(
    Guid Id,
    DocumentId DocumentId,
    string DocumentHash,
    int PageIndex,
    AnnotationKind Kind,
    string? SelectedText,
    int SourceStart,
    int SourceLength,
    string? TextFingerprint,
    string? PrefixContext,
    string? SuffixContext,
    IReadOnlyList<NormalizedPdfRectangle> Rectangles,
    string? NoteText,
    string? LinkedTranslation,
    string Color,
    AnnotationAnchorStatus AnchorStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsTextAnchor => Kind != AnnotationKind.Bookmark;
}

public sealed record AnnotationResolution(
    DocumentAnnotation Annotation,
    bool WasChanged,
    string Message);
