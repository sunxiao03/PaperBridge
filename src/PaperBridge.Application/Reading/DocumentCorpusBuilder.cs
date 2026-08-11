using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Bilingual;

namespace PaperBridge.Application.Reading;

public static class DocumentCorpusBuilder
{
    public const int MaximumPages = 2_000;
    public const int MaximumDocumentCharacters = 10_000_000;
    public const int MaximumChunks = 10_000;

    public static DocumentCorpus Build(
        IReadOnlyList<PdfPageText> pages,
        IReadOnlyList<PdfOutlineItem> outline)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(outline);
        if (pages.Count is < 1 or > MaximumPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pages), $"Document must contain 1–{MaximumPages} pages.");
        }

        if (pages.Select((page, index) => page.PageIndex == index).Any(matches => !matches))
        {
            throw new ArgumentException("Pages must be complete and ordered by page index.", nameof(pages));
        }

        var totalCharacters = pages.Sum(page => (long)page.Text.Length);
        if (totalCharacters > MaximumDocumentCharacters)
        {
            throw new InvalidOperationException($"文档文本超过 {MaximumDocumentCharacters:N0} 字符的本地处理上限。");
        }

        var sections = BuildSections(pages.Count, outline);
        var chunks = new List<DocumentTextChunk>();
        foreach (var page in pages)
        {
            var section = sections.Last(section => section.StartPageIndex <= page.PageIndex);
            var analysis = PageLayoutAnalyzer.Analyze(page);
            foreach (var paragraph in analysis.Paragraphs)
            {
                var text = Normalize(paragraph.Text);
                if (text.Length == 0)
                {
                    continue;
                }

                if (chunks.Count >= MaximumChunks)
                {
                    throw new InvalidOperationException($"文档分块超过 {MaximumChunks:N0} 条的本地处理上限。");
                }

                chunks.Add(new DocumentTextChunk(
                    $"p{page.PageIndex + 1}-s{paragraph.SourceStart}-l{paragraph.SourceLength}",
                    page.PageIndex,
                    section.Title,
                    paragraph.SourceStart,
                    text));
            }
        }

        return new DocumentCorpus(pages.Count, sections, chunks, checked((int)totalCharacters));
    }

    public static async Task<DocumentCorpus> BuildAsync(
        IPdfDocument document,
        IReadOnlyList<PdfOutlineItem> outline,
        IProgress<int>? completedPages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(outline);
        if (document.PageCount is < 1 or > MaximumPages)
        {
            throw new ArgumentOutOfRangeException(nameof(document), $"Document must contain 1–{MaximumPages} pages.");
        }

        var sections = BuildSections(document.PageCount, outline);
        var chunks = new List<DocumentTextChunk>();
        var totalCharacters = 0;
        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await document.ExtractPageTextAsync(pageIndex, cancellationToken);
            totalCharacters = checked(totalCharacters + page.Text.Length);
            if (totalCharacters > MaximumDocumentCharacters)
            {
                throw new InvalidOperationException($"文档文本超过 {MaximumDocumentCharacters:N0} 字符的本地处理上限。");
            }

            var section = sections.Last(section => section.StartPageIndex <= pageIndex);
            var analysis = PageLayoutAnalyzer.Analyze(page);
            foreach (var paragraph in analysis.Paragraphs)
            {
                var text = Normalize(paragraph.Text);
                if (text.Length == 0)
                {
                    continue;
                }

                if (chunks.Count >= MaximumChunks)
                {
                    throw new InvalidOperationException($"文档分块超过 {MaximumChunks:N0} 条的本地处理上限。");
                }

                chunks.Add(new DocumentTextChunk(
                    $"p{pageIndex + 1}-s{paragraph.SourceStart}-l{paragraph.SourceLength}",
                    pageIndex,
                    section.Title,
                    paragraph.SourceStart,
                    text));
            }

            completedPages?.Report(pageIndex + 1);
        }

        return new DocumentCorpus(document.PageCount, sections, chunks, totalCharacters);
    }

    private static IReadOnlyList<DocumentSection> BuildSections(
        int pageCount,
        IReadOnlyList<PdfOutlineItem> outline)
    {
        var anchors = new List<(int PageIndex, string Title, int Sequence)>();
        var sequence = 0;

        void Visit(IEnumerable<PdfOutlineItem> items, string? parent)
        {
            foreach (var item in items)
            {
                var title = Normalize(item.Title);
                var path = string.IsNullOrWhiteSpace(parent) ? title : $"{parent} > {title}";
                if (item.PageIndex is int pageIndex && pageIndex >= 0 && pageIndex < pageCount && title.Length > 0)
                {
                    anchors.Add((pageIndex, path, sequence++));
                }

                Visit(item.Children, path);
            }
        }

        Visit(outline, null);
        var starts = anchors
            .OrderBy(anchor => anchor.PageIndex)
            .ThenBy(anchor => anchor.Sequence)
            .GroupBy(anchor => anchor.PageIndex)
            .Select(group => group.Last())
            .ToList();
        if (starts.Count == 0)
        {
            return Enumerable.Range(0, pageCount)
                .Select(page => new DocumentSection($"第 {page + 1} 页（PDF 无目录）", page, page + 1))
                .ToArray();
        }

        if (starts[0].PageIndex > 0)
        {
            starts.Insert(0, (0, "文档开头（目录前）", -1));
        }

        var result = new List<DocumentSection>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var end = index + 1 < starts.Count ? starts[index + 1].PageIndex : pageCount;
            result.Add(new DocumentSection(starts[index].Title, starts[index].PageIndex, Math.Max(starts[index].PageIndex + 1, end)));
        }

        return result;
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
