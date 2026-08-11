using PaperBridge.Application.Annotations;

namespace PaperBridge.App;

public sealed class AnnotationViewModel
{
    public AnnotationViewModel(DocumentAnnotation annotation, string resolutionMessage)
    {
        Annotation = annotation;
        ResolutionMessage = resolutionMessage;
    }

    public DocumentAnnotation Annotation { get; }

    public string ResolutionMessage { get; }

    public string PageLabel => $"第 {Annotation.PageIndex + 1} 页";

    public string KindText => Annotation.Kind switch
    {
        AnnotationKind.Highlight => "高亮",
        AnnotationKind.Underline => "下划线",
        AnnotationKind.Note => "批注",
        AnnotationKind.Bookmark => "书签",
        _ => Annotation.Kind.ToString()
    };

    public string Preview => Annotation.SelectedText ?? Annotation.NoteText ?? "页面书签";

    public string Detail => string.Join(" · ", new[]
    {
        Annotation.NoteText,
        string.IsNullOrWhiteSpace(Annotation.LinkedTranslation) ? null : "含关联译文",
        StatusText
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string StatusText => Annotation.AnchorStatus switch
    {
        AnnotationAnchorStatus.Valid => "有效",
        AnnotationAnchorStatus.Migrated => "已安全迁移",
        AnnotationAnchorStatus.Orphaned => "已失效",
        AnnotationAnchorStatus.DocumentChanged => "文档已变化",
        _ => Annotation.AnchorStatus.ToString()
    };
}

public sealed record AnnotationOverlayItem(
    Guid AnnotationId,
    AnnotationKind Kind,
    string Color,
    IReadOnlyList<NormalizedPdfRectangle> Rectangles,
    AnnotationAnchorStatus Status);
