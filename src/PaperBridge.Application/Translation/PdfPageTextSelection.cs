using PaperBridge.Application.Abstractions;

namespace PaperBridge.Application.Translation;

/// <summary>
/// Resolves a visual PDF character range back to the exact UTF-16 range in the
/// extracted page text. Leading and trailing whitespace are excluded so saved
/// annotation anchors and translation requests refer to the same characters.
/// </summary>
public static class PdfPageTextSelection
{
    public static PdfPageTextSelectionRange Resolve(PdfPageText page, int firstCharacterIndex, int lastCharacterIndex)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Characters.Count == 0)
        {
            return PdfPageTextSelectionRange.Empty;
        }

        var startIndex = Math.Clamp(Math.Min(firstCharacterIndex, lastCharacterIndex), 0, page.Characters.Count - 1);
        var endIndex = Math.Clamp(Math.Max(firstCharacterIndex, lastCharacterIndex), 0, page.Characters.Count - 1);
        var rawText = string.Concat(page.Characters.Skip(startIndex).Take(endIndex - startIndex + 1).Select(item => item.Text));
        var pageOffset = page.Characters.Take(startIndex).Sum(item => item.Text.Length);
        var leadingWhitespace = rawText.TakeWhile(char.IsWhiteSpace).Count();
        var trailingWhitespace = rawText.Reverse().TakeWhile(char.IsWhiteSpace).Count();
        var length = Math.Max(0, rawText.Length - leadingWhitespace - trailingWhitespace);
        if (length == 0)
        {
            return PdfPageTextSelectionRange.Empty;
        }

        return new PdfPageTextSelectionRange(
            pageOffset + leadingWhitespace,
            length,
            rawText.Substring(leadingWhitespace, length));
    }
}

public sealed record PdfPageTextSelectionRange(int Start, int Length, string Text)
{
    public static PdfPageTextSelectionRange Empty { get; } = new(0, 0, string.Empty);
}
