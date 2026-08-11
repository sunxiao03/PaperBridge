using System.Security.Cryptography;
using System.Text;
using PaperBridge.Application.Abstractions;
using PaperBridge.Domain.Documents;

namespace PaperBridge.Application.Annotations;

public static class AnnotationAnchorService
{
    private const int ContextLength = 40;
    private const int MaximumRectangles = 256;

    public static DocumentAnnotation CreateTextAnnotation(
        DocumentId documentId,
        string documentHash,
        PdfPageText page,
        int selectionStart,
        int selectionLength,
        AnnotationKind kind,
        string color,
        string? noteText = null,
        string? linkedTranslation = null)
    {
        if (kind == AnnotationKind.Bookmark)
        {
            throw new ArgumentException("Use CreateBookmark for page bookmarks.", nameof(kind));
        }

        ValidateDocumentHash(documentHash);
        ArgumentNullException.ThrowIfNull(page);
        if (selectionStart < 0 || selectionLength <= 0 || selectionStart + selectionLength > page.Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        }

        var selected = page.Text.Substring(selectionStart, selectionLength).Trim();
        if (selected.Length is < 1 or > 10_000)
        {
            throw new ArgumentException("Selected text must contain 1–10,000 characters.", nameof(selectionLength));
        }

        var actualStart = page.Text.IndexOf(selected, selectionStart, selectionLength, StringComparison.Ordinal);
        var rectangles = BuildRectangles(page, actualStart, selected.Length);
        if (rectangles.Count == 0)
        {
            throw new InvalidOperationException("选区没有可用的 PDF 坐标，无法创建可靠锚点。");
        }

        var now = DateTimeOffset.UtcNow;
        return new DocumentAnnotation(
            Guid.NewGuid(),
            documentId,
            documentHash,
            page.PageIndex,
            kind,
            selected,
            actualStart,
            selected.Length,
            Fingerprint(selected),
            ExtractContext(page.Text, Math.Max(0, actualStart - ContextLength), actualStart),
            ExtractContext(page.Text, actualStart + selected.Length, Math.Min(page.Text.Length, actualStart + selected.Length + ContextLength)),
            rectangles,
            Normalize(noteText),
            Normalize(linkedTranslation),
            ValidateColor(color),
            AnnotationAnchorStatus.Valid,
            now,
            now);
    }

    public static DocumentAnnotation CreateBookmark(
        DocumentId documentId,
        string documentHash,
        int pageIndex,
        string color,
        string? noteText = null,
        string? linkedTranslation = null)
    {
        ValidateDocumentHash(documentHash);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var now = DateTimeOffset.UtcNow;
        return new DocumentAnnotation(
            Guid.NewGuid(), documentId, documentHash, pageIndex, AnnotationKind.Bookmark,
            null, 0, 0, null, null, null, [], Normalize(noteText), Normalize(linkedTranslation),
            ValidateColor(color), AnnotationAnchorStatus.Valid, now, now);
    }

    public static AnnotationResolution Resolve(
        DocumentAnnotation annotation,
        string currentDocumentHash,
        PdfPageText? page)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ValidateDocumentHash(currentDocumentHash);
        if (!string.Equals(annotation.DocumentHash, currentDocumentHash, StringComparison.OrdinalIgnoreCase))
        {
            if (annotation.AnchorStatus == AnnotationAnchorStatus.DocumentChanged)
            {
                return new AnnotationResolution(annotation, false, "文档内容哈希已变化，未自动套用旧锚点。");
            }

            var changed = annotation with
            {
                AnchorStatus = AnnotationAnchorStatus.DocumentChanged,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            return new AnnotationResolution(changed, changed != annotation, "文档内容哈希已变化，未自动套用旧锚点。");
        }

        if (!annotation.IsTextAnchor)
        {
            var validBookmark = annotation with { AnchorStatus = AnnotationAnchorStatus.Valid };
            return new AnnotationResolution(validBookmark, validBookmark != annotation, "页面书签有效。");
        }

        if (page is null || page.PageIndex != annotation.PageIndex || string.IsNullOrWhiteSpace(annotation.SelectedText))
        {
            return Orphan(annotation, "锚点页面或原文不可用。");
        }

        if (annotation.SourceStart >= 0 && annotation.SourceLength > 0 &&
            annotation.SourceStart + annotation.SourceLength <= page.Text.Length)
        {
            var current = page.Text.Substring(annotation.SourceStart, annotation.SourceLength);
            if (string.Equals(Fingerprint(current), annotation.TextFingerprint, StringComparison.Ordinal))
            {
                var rectangles = BuildRectangles(page, annotation.SourceStart, annotation.SourceLength);
                if (rectangles.Count > 0)
                {
                    var resolvedStatus = annotation.AnchorStatus == AnnotationAnchorStatus.Migrated
                        ? AnnotationAnchorStatus.Migrated
                        : AnnotationAnchorStatus.Valid;
                    var rectanglesChanged = !annotation.Rectangles.SequenceEqual(rectangles);
                    var resolutionChanged = annotation.AnchorStatus != resolvedStatus || rectanglesChanged;
                    var valid = annotation with
                    {
                        Rectangles = rectanglesChanged ? rectangles : annotation.Rectangles,
                        AnchorStatus = resolvedStatus,
                        UpdatedAtUtc = resolutionChanged ? DateTimeOffset.UtcNow : annotation.UpdatedAtUtc
                    };
                    return new AnnotationResolution(valid, valid != annotation, "原文指纹和坐标均有效。");
                }
            }
        }

        var matches = FindAll(page.Text, annotation.SelectedText).Take(2).ToArray();
        if (matches.Length != 1)
        {
            return Orphan(annotation, matches.Length == 0
                ? "原文已不存在，锚点失效。"
                : "原文出现多次，无法安全决定迁移位置。");
        }

        var newRectangles = BuildRectangles(page, matches[0], annotation.SelectedText.Length);
        if (newRectangles.Count == 0)
        {
            return Orphan(annotation, "找到原文但无法恢复 PDF 坐标。");
        }

        var migrated = annotation with
        {
            SourceStart = matches[0],
            SourceLength = annotation.SelectedText.Length,
            Rectangles = newRectangles,
            PrefixContext = ExtractContext(page.Text, Math.Max(0, matches[0] - ContextLength), matches[0]),
            SuffixContext = ExtractContext(
                page.Text,
                matches[0] + annotation.SelectedText.Length,
                Math.Min(page.Text.Length, matches[0] + annotation.SelectedText.Length + ContextLength)),
            AnchorStatus = AnnotationAnchorStatus.Migrated,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return new AnnotationResolution(migrated, true, "原偏移失效；依据同页唯一原文匹配安全迁移。");
    }

    public static string Fingerprint(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var canonical = string.Join(' ', text
            .Trim()
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static IReadOnlyList<NormalizedPdfRectangle> BuildRectangles(
        PdfPageText page,
        int start,
        int length)
    {
        if (page.WidthInPoints <= 0 || page.HeightInPoints <= 0)
        {
            return [];
        }

        var located = new List<PdfRectangle>();
        var textOffset = 0;
        foreach (var character in page.Characters)
        {
            var next = textOffset + character.Text.Length;
            if (next > start && textOffset < start + length && character.Bounds is { Width: > 0, Height: > 0 } bounds)
            {
                located.Add(bounds);
            }

            textOffset = next;
        }

        if (located.Count == 0)
        {
            return [];
        }

        var merged = new List<PdfRectangle>();
        foreach (var bounds in located)
        {
            if (merged.Count > 0 && CanMerge(merged[^1], bounds))
            {
                var previous = merged[^1];
                merged[^1] = new PdfRectangle(
                    Math.Min(previous.Left, bounds.Left),
                    Math.Min(previous.Bottom, bounds.Bottom),
                    Math.Max(previous.Right, bounds.Right),
                    Math.Max(previous.Top, bounds.Top));
            }
            else
            {
                merged.Add(bounds);
            }
        }

        return merged.Take(MaximumRectangles).Select(bounds => new NormalizedPdfRectangle(
            Math.Clamp(bounds.Left / page.WidthInPoints, 0, 1),
            Math.Clamp((page.HeightInPoints - bounds.Top) / page.HeightInPoints, 0, 1),
            Math.Clamp(bounds.Width / page.WidthInPoints, 0.000001, 1),
            Math.Clamp(bounds.Height / page.HeightInPoints, 0.000001, 1)).Validate()).ToArray();
    }

    private static bool CanMerge(PdfRectangle left, PdfRectangle right)
    {
        var verticalOverlap = Math.Max(0, Math.Min(left.Top, right.Top) - Math.Max(left.Bottom, right.Bottom));
        var minimumHeight = Math.Max(0.001, Math.Min(left.Height, right.Height));
        var gap = right.Left - left.Right;
        return verticalOverlap / minimumHeight >= 0.55 && gap >= -1 && gap <= Math.Max(left.Height, right.Height) * 1.5;
    }

    private static IEnumerable<int> FindAll(string text, string value)
    {
        var start = 0;
        while (start <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                yield break;
            }

            yield return index;
            start = index + Math.Max(1, value.Length);
        }
    }

    private static AnnotationResolution Orphan(DocumentAnnotation annotation, string message)
    {
        if (annotation.AnchorStatus == AnnotationAnchorStatus.Orphaned)
        {
            return new AnnotationResolution(annotation, false, message);
        }

        var orphaned = annotation with
        {
            AnchorStatus = AnnotationAnchorStatus.Orphaned,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return new AnnotationResolution(orphaned, orphaned != annotation, message);
    }

    private static string? ExtractContext(string text, int start, int end)
    {
        if (end <= start)
        {
            return null;
        }

        return Normalize(text[start..end]);
    }

    private static string ValidateColor(string color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        var value = color.Trim();
        if (value.Length != 7 || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Color must use #RRGGBB format.", nameof(color));
        }

        return value.ToUpperInvariant();
    }

    private static void ValidateDocumentHash(string documentHash)
    {
        if (string.IsNullOrWhiteSpace(documentHash) || documentHash.Length != 64 || !documentHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Document hash must be a SHA-256 hexadecimal value.", nameof(documentHash));
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
