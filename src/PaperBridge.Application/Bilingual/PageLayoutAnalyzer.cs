using System.Text;
using PaperBridge.Application.Abstractions;

namespace PaperBridge.Application.Bilingual;

public static class PageLayoutAnalyzer
{
    public const double ParagraphConfidenceThreshold = 0.72;
    public const int MaximumParagraphsPerPage = 80;
    public const int MaximumParagraphCharacters = 3_000;

    public static PageLayoutAnalysis Analyze(PdfPageText page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var text = page.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PageLayoutAnalysis(
                page.PageIndex,
                BilingualLayoutMode.PageAligned,
                0,
                "页面没有可用文本层。",
                [],
                false,
                0,
                false);
        }

        var lines = BuildLines(text, page.Characters);
        var nonEmpty = lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray();
        var boundedCharacters = page.Characters.Count(character => character.Bounds is not null);
        var boundsCoverage = page.Characters.Count == 0 ? 0 : (double)boundedCharacters / page.Characters.Count;
        var formulaDensity = CalculateFormulaDensity(text);
        var hasColumns = DetectMultipleColumns(nonEmpty, page.WidthInPoints, page.HeightInPoints);
        var mayCrossPage = MayCrossPage(nonEmpty);
        var paragraphs = BuildParagraphs(page.PageIndex, nonEmpty);

        var confidence = 0.35 + (0.45 * Math.Clamp(boundsCoverage, 0, 1));
        confidence -= hasColumns ? 0.32 : 0;
        confidence -= Math.Min(0.25, formulaDensity * 0.7);
        confidence -= mayCrossPage ? 0.08 : 0;
        confidence -= paragraphs.Count > MaximumParagraphsPerPage ? 0.18 : 0;
        confidence = Math.Clamp(confidence, 0, 1);

        var reasons = new List<string>();
        if (boundsCoverage < 0.65)
        {
            reasons.Add("文本坐标覆盖不足");
        }

        if (hasColumns)
        {
            reasons.Add("检测到可能的多栏版面");
        }

        if (formulaDensity > 0.18)
        {
            reasons.Add("公式或符号密度较高");
        }

        if (mayCrossPage)
        {
            reasons.Add("存在跨页段落迹象");
        }

        if (paragraphs.Count > MaximumParagraphsPerPage)
        {
            reasons.Add("段落碎片过多");
        }

        var mode = confidence >= ParagraphConfidenceThreshold && !hasColumns
            ? BilingualLayoutMode.Paragraph
            : BilingualLayoutMode.PageAligned;
        if (mode == BilingualLayoutMode.PageAligned && reasons.Count == 0)
        {
            reasons.Add("版面置信度低于段落对齐阈值");
        }

        if (mode == BilingualLayoutMode.PageAligned)
        {
            paragraphs = BuildPageSegment(page.PageIndex, text, page.Characters);
        }

        return new PageLayoutAnalysis(
            page.PageIndex,
            mode,
            confidence,
            reasons.Count == 0 ? null : string.Join("；", reasons),
            paragraphs,
            hasColumns,
            formulaDensity,
            mayCrossPage);
    }

    private static List<SourceLine> BuildLines(string text, IReadOnlyList<PdfTextCharacter> characters)
    {
        var lines = new List<SourceLine>();
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            var length = index - start;
            var value = text.Substring(start, length).Trim();
            var bounds = GetBounds(characters, start, length);
            lines.Add(new SourceLine(start, length, value, bounds));
            if (index < text.Length && text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        return lines;
    }

    private static IReadOnlyList<SourceParagraph> BuildParagraphs(int pageIndex, IReadOnlyList<SourceLine> lines)
    {
        var result = new List<SourceParagraph>();
        var group = new List<SourceLine>();
        var characterCount = 0;

        void Flush()
        {
            if (group.Count == 0)
            {
                return;
            }

            var text = string.Join(' ', group.Select(line => line.Text)).Trim();
            if (text.Length > 0)
            {
                var start = group[0].Start;
                var end = group[^1].Start + group[^1].Length;
                var bounds = Union(group.Select(line => line.Bounds));
                result.Add(CreateParagraph(pageIndex, result.Count, start, end - start, text, bounds));
            }

            group.Clear();
            characterCount = 0;
        }

        foreach (var line in lines)
        {
            if (line.Text.Length == 0)
            {
                Flush();
                continue;
            }

            group.Add(line);
            characterCount += line.Text.Length + 1;
            if (characterCount >= MaximumParagraphCharacters || EndsParagraph(line.Text))
            {
                Flush();
            }
        }

        Flush();
        return result;
    }

    private static IReadOnlyList<SourceParagraph> BuildPageSegment(
        int pageIndex,
        string text,
        IReadOnlyList<PdfTextCharacter> characters)
    {
        var normalized = string.Join('\n', text
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(line => line.Trim()))
            .Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        var result = new List<SourceParagraph>();
        var offset = 0;
        while (offset < normalized.Length)
        {
            var length = Math.Min(MaximumParagraphCharacters, normalized.Length - offset);
            if (offset + length < normalized.Length)
            {
                var boundary = normalized.LastIndexOfAny(['.', '!', '?', ';', '\n'], offset + length - 1, length);
                if (boundary >= offset + Math.Min(500, length / 2))
                {
                    length = boundary - offset + 1;
                }
            }

            var chunk = normalized.Substring(offset, length).Trim();
            if (chunk.Length > 0)
            {
                result.Add(CreateParagraph(
                    pageIndex,
                    result.Count,
                    offset,
                    length,
                    chunk,
                    GetBounds(characters, offset, length)));
            }

            offset += Math.Max(1, length);
        }

        return result;
    }

    private static bool DetectMultipleColumns(
        IReadOnlyList<SourceLine> lines,
        double pageWidth,
        double pageHeight)
    {
        if (pageWidth <= 0 || lines.Count < 8)
        {
            return false;
        }

        var located = lines.Where(line => line.Bounds is not null).Select(line => line.Bounds!).ToArray();
        if (located.Length < 8)
        {
            return false;
        }

        var left = located.Where(bounds => bounds.Right <= pageWidth * 0.62).ToArray();
        var right = located.Where(bounds => bounds.Left >= pageWidth * 0.38).ToArray();
        if (left.Length < 3 || right.Length < 3)
        {
            return false;
        }

        var leftRange = VerticalRange(left);
        var rightRange = VerticalRange(right);
        var overlap = Math.Max(0, Math.Min(leftRange.Top, rightRange.Top) - Math.Max(leftRange.Bottom, rightRange.Bottom));
        var reference = Math.Max(1, Math.Min(leftRange.Top - leftRange.Bottom, rightRange.Top - rightRange.Bottom));
        return overlap / reference >= 0.35 &&
            left.Average(bounds => bounds.Right) < right.Average(bounds => bounds.Left);
    }

    private static double CalculateFormulaDensity(string text)
    {
        var nonWhitespace = text.Count(character => !char.IsWhiteSpace(character));
        if (nonWhitespace == 0)
        {
            return 0;
        }

        const string formulaSymbols = "=±×÷∑∫√∞≈≠≤≥αβγδεζηθλμνξπρστφχψω_{}[]^";
        var formula = text.Count(character => formulaSymbols.Contains(char.ToLowerInvariant(character)) ||
            char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.MathSymbol);
        return (double)formula / nonWhitespace;
    }

    private static bool MayCrossPage(IReadOnlyList<SourceLine> lines)
    {
        if (lines.Count == 0)
        {
            return false;
        }

        var first = lines[0].Text.TrimStart();
        var last = lines[^1].Text.TrimEnd();
        var beginsMidSentence = first.Length > 0 && char.IsLower(first[0]);
        var endsMidSentence = last.Length > 0 && last[^1] is not ('.' or '!' or '?' or ':' or ';' or ')' or ']');
        return beginsMidSentence || endsMidSentence;
    }

    private static bool EndsParagraph(string text) =>
        text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('!') || text.TrimEnd().EndsWith('?');

    private static PdfRectangle? GetBounds(IReadOnlyList<PdfTextCharacter> characters, int start, int length)
    {
        if (length <= 0 || start >= characters.Count)
        {
            return null;
        }

        return Union(characters
            .Skip(Math.Max(0, start))
            .Take(Math.Min(length, Math.Max(0, characters.Count - start)))
            .Select(character => character.Bounds));
    }

    private static PdfRectangle? Union(IEnumerable<PdfRectangle?> bounds)
    {
        var values = bounds.OfType<PdfRectangle>().ToArray();
        return values.Length == 0
            ? null
            : new PdfRectangle(
                values.Min(value => value.Left),
                values.Min(value => value.Bottom),
                values.Max(value => value.Right),
                values.Max(value => value.Top));
    }

    private static SourceParagraph CreateParagraph(
        int pageIndex,
        int index,
        int start,
        int length,
        string text,
        PdfRectangle? bounds) =>
        new(
            $"p{pageIndex + 1:D5}-{index + 1:D4}",
            pageIndex,
            start,
            length,
            text,
            bounds?.Left,
            bounds?.Top,
            bounds?.Width,
            bounds?.Height);

    private static (double Bottom, double Top) VerticalRange(IReadOnlyList<PdfRectangle> values) =>
        (values.Min(value => value.Bottom), values.Max(value => value.Top));

    private sealed record SourceLine(int Start, int Length, string Text, PdfRectangle? Bounds);
}
