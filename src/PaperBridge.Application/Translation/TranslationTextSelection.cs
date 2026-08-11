using PaperBridge.Domain.Translations;

namespace PaperBridge.Application.Translation;

public static class TranslationTextSelection
{
    public static TranslationTextRange Resolve(
        string text,
        int selectionStart,
        int selectionLength,
        int caretIndex,
        TranslationGranularity granularity)
    {
        text ??= string.Empty;
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - selectionStart);
        if (granularity == TranslationGranularity.Page)
        {
            return TrimRange(text, 0, text.Length);
        }

        if (selectionLength > 0)
        {
            return TrimRange(text, selectionStart, selectionLength);
        }

        if (granularity == TranslationGranularity.Selection || text.Length == 0)
        {
            return new TranslationTextRange(string.Empty, selectionStart, 0);
        }

        var caret = Math.Clamp(caretIndex, 0, text.Length - 1);
        var (start, length) = granularity switch
        {
            TranslationGranularity.Word => FindDelimitedRange(text, caret, IsWordCharacter, false),
            TranslationGranularity.Sentence => FindSentenceRange(text, caret),
            TranslationGranularity.Paragraph => FindParagraphRange(text, caret),
            _ => (0, 0)
        };
        return TrimRange(text, start, length);
    }

    private static (int Start, int Length) FindDelimitedRange(
        string text,
        int caret,
        Func<char, bool> isContent,
        bool includeSentencePunctuation)
    {
        while (caret < text.Length && !isContent(text[caret]) && caret + 1 < text.Length)
        {
            caret++;
        }

        if (!isContent(text[caret]))
        {
            return (caret, 0);
        }

        var start = caret;
        while (start > 0 && isContent(text[start - 1]))
        {
            start--;
        }

        var end = caret;
        while (end < text.Length && isContent(text[end]))
        {
            end++;
        }

        if (includeSentencePunctuation && end < text.Length && text[end] is '.' or '!' or '?')
        {
            end++;
        }

        return (start, end - start);
    }

    private static (int Start, int Length) FindParagraphRange(string text, int caret)
    {
        var startSeparator = text.LastIndexOf("\n\n", Math.Max(0, caret - 1), StringComparison.Ordinal);
        var start = startSeparator < 0 ? 0 : startSeparator + 2;
        var endSeparator = text.IndexOf("\n\n", caret, StringComparison.Ordinal);
        var end = endSeparator < 0 ? text.Length : endSeparator;
        return (start, end - start);
    }

    private static (int Start, int Length) FindSentenceRange(string text, int caret)
    {
        var start = caret;
        while (start > 0 && !IsSentenceBoundary(text, start - 1))
        {
            start--;
        }

        var end = caret;
        while (end < text.Length && !IsSentenceBoundary(text, end))
        {
            end++;
        }

        if (end < text.Length && text[end] is '.' or '!' or '?')
        {
            end++;
        }

        return (start, end - start);
    }

    private static bool IsSentenceBoundary(string text, int index)
    {
        var character = text[index];
        if (character is '\n' or '\r' or '!' or '?')
        {
            return true;
        }

        if (character != '.')
        {
            return false;
        }

        return index == 0 || index + 1 >= text.Length ||
               !char.IsDigit(text[index - 1]) || !char.IsDigit(text[index + 1]);
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '-' or '\'';

    private static TranslationTextRange TrimRange(string text, int start, int length)
    {
        var end = start + length;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return new TranslationTextRange(text[start..end], start, end - start);
    }
}

public sealed record TranslationTextRange(string Text, int Start, int Length);
