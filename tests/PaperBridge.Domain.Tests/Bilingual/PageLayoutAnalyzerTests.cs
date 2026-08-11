using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Bilingual;

namespace PaperBridge.Domain.Tests.Bilingual;

public sealed class PageLayoutAnalyzerTests
{
    [Fact]
    public void Analyze_SingleColumnScientificTextProducesParagraphAnchors()
    {
        var page = CreatePage([
            ("Neutron transport describes particle motion in a reactor core.", 60d, 740d),
            ("The effective multiplication factor determines criticality.", 60d, 720d),
            ("Reactivity feedback influences transient behavior.", 60d, 680d),
            ("Delayed neutrons make reactor control possible.", 60d, 660d)
        ]);

        var result = PageLayoutAnalyzer.Analyze(page);

        Assert.Equal(BilingualLayoutMode.Paragraph, result.RecommendedMode);
        Assert.True(result.Confidence >= PageLayoutAnalyzer.ParagraphConfidenceThreshold);
        Assert.NotEmpty(result.Paragraphs);
        Assert.All(result.Paragraphs, paragraph => Assert.StartsWith("p00001-", paragraph.SegmentId));
    }

    [Fact]
    public void Analyze_TwoColumnsDegradesToPageAlignedWithExplicitReason()
    {
        var page = CreatePage([
            ("Left column line one.", 50d, 740d),
            ("Right column line one.", 330d, 740d),
            ("Left column line two.", 50d, 710d),
            ("Right column line two.", 330d, 710d),
            ("Left column line three.", 50d, 680d),
            ("Right column line three.", 330d, 680d),
            ("Left column line four.", 50d, 650d),
            ("Right column line four.", 330d, 650d)
        ]);

        var result = PageLayoutAnalyzer.Analyze(page);

        Assert.True(result.HasMultipleColumns);
        Assert.Equal(BilingualLayoutMode.PageAligned, result.RecommendedMode);
        Assert.Contains("多栏", result.DegradationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_EmptyTextNeverClaimsParagraphAlignment()
    {
        var result = PageLayoutAnalyzer.Analyze(new PdfPageText(2, 600, 800, string.Empty, [], []));

        Assert.Equal(BilingualLayoutMode.PageAligned, result.RecommendedMode);
        Assert.Empty(result.Paragraphs);
        Assert.Equal(0, result.Confidence);
    }

    private static PdfPageText CreatePage(IReadOnlyList<(string Text, double Left, double Top)> lines)
    {
        var text = string.Join('\n', lines.Select(line => line.Text));
        var characters = new List<PdfTextCharacter>();
        var sourceIndex = 0;
        foreach (var line in lines)
        {
            for (var index = 0; index < line.Text.Length; index++)
            {
                var left = line.Left + (index * 5);
                characters.Add(new PdfTextCharacter(
                    sourceIndex++,
                    line.Text[index].ToString(),
                    new PdfRectangle(left, line.Top - 10, left + 4.5, line.Top)));
            }

            if (sourceIndex < text.Length)
            {
                characters.Add(new PdfTextCharacter(sourceIndex++, "\n", null));
            }
        }

        return new PdfPageText(0, 600, 800, text, characters, []);
    }
}
