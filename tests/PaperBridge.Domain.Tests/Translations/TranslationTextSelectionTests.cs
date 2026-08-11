using PaperBridge.Application.Translation;
using PaperBridge.Domain.Translations;

namespace PaperBridge.Domain.Tests.Translations;

public sealed class TranslationTextSelectionTests
{
    private const string Text = "First sentence. The k-effective value is 1.0!\n\nSecond paragraph here.";

    [Fact]
    public void Resolve_UsesExplicitSelectionForNonPageGranularity()
    {
        var range = TranslationTextSelection.Resolve(Text, 6, 8, 0, TranslationGranularity.Selection);

        Assert.Equal("sentence", range.Text);
        Assert.Equal(6, range.Start);
    }

    [Fact]
    public void Resolve_FindsWordAtCaret()
    {
        var range = TranslationTextSelection.Resolve(
            Text,
            0,
            0,
            Text.IndexOf("effective", StringComparison.Ordinal),
            TranslationGranularity.Word);

        Assert.Equal("k-effective", range.Text);
    }

    [Fact]
    public void Resolve_FindsSentenceAndIncludesTerminalPunctuation()
    {
        var range = TranslationTextSelection.Resolve(
            Text,
            0,
            0,
            Text.IndexOf("value", StringComparison.Ordinal),
            TranslationGranularity.Sentence);

        Assert.Equal("The k-effective value is 1.0!", range.Text);
    }

    [Fact]
    public void Resolve_FindsParagraphBetweenBlankLines()
    {
        var range = TranslationTextSelection.Resolve(Text, 0, 0, 5, TranslationGranularity.Paragraph);

        Assert.Equal("First sentence. The k-effective value is 1.0!", range.Text);
    }

    [Fact]
    public void Resolve_PageIgnoresSelectionAndReturnsAllTrimmedText()
    {
        var range = TranslationTextSelection.Resolve($"  {Text}  ", 2, 5, 2, TranslationGranularity.Page);

        Assert.Equal(Text, range.Text);
    }
}
