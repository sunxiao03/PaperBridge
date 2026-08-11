using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Translation;
using PaperBridge.Infrastructure.Pdf;

namespace PaperBridge.Infrastructure.Tests.Pdf;

public sealed class PdfPageTextSelectionTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "pdfium-text-layer-sample.pdf");

    [Fact]
    public void Resolve_MapsVisualCharactersToExactPageRange()
    {
        var page = CreatePage("Alpha beta gamma");

        var selection = PdfPageTextSelection.Resolve(page, 6, 9);

        Assert.Equal(6, selection.Start);
        Assert.Equal(4, selection.Length);
        Assert.Equal("beta", selection.Text);
        Assert.Equal(selection.Text, page.Text.Substring(selection.Start, selection.Length));
    }

    [Fact]
    public void Resolve_NormalizesReverseDragAndTrimsWhitespace()
    {
        var page = CreatePage("  selected text \r\n");

        var selection = PdfPageTextSelection.Resolve(page, page.Characters.Count - 1, 0);

        Assert.Equal(2, selection.Start);
        Assert.Equal("selected text", selection.Text);
        Assert.Equal(selection.Text.Length, selection.Length);
        Assert.Equal(selection.Text, page.Text.Substring(selection.Start, selection.Length));
    }

    [Fact]
    public void Resolve_ReturnsEmptyForWhitespaceOnlyRange()
    {
        var page = CreatePage(" \r\n");

        Assert.Equal(PdfPageTextSelectionRange.Empty, PdfPageTextSelection.Resolve(page, 0, 2));
    }

    [Fact]
    public async Task Resolve_UsesRealPdfiumTextLayerWithoutChangingTheSource()
    {
        const string expected = "The effective multiplication factor is unity.";
        await using var document = PdfiumDocument.Open(FixturePath);
        var page = await document.ExtractPageTextAsync(0);
        var joinedCharacters = string.Concat(page.Characters.Select(item => item.Text));
        var start = joinedCharacters.IndexOf(expected, StringComparison.Ordinal);

        Assert.True(start >= 0);
        var selection = PdfPageTextSelection.Resolve(page, start, start + expected.Length - 1);

        Assert.Equal(expected, selection.Text);
        Assert.Equal(expected, page.Text.Substring(selection.Start, selection.Length));
        Assert.All(
            page.Characters.Skip(start).Take(expected.Length).Where(item => !char.IsWhiteSpace(item.Text[0])),
            item => Assert.NotNull(item.Bounds));
    }

    private static PdfPageText CreatePage(string text)
    {
        var characters = text.Select((value, index) => new PdfTextCharacter(
            index,
            value.ToString(),
            new PdfRectangle(index * 5, 0, index * 5 + 4, 10))).ToArray();
        return new PdfPageText(0, 600, 800, text, characters, []);
    }
}
