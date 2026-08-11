using System.Text.RegularExpressions;

namespace PaperBridge.Application.Reading;

public static partial class CitationValidator
{
    public static CitationValidationResult Validate(
        string answer,
        IReadOnlyList<EvidenceCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        ArgumentNullException.ThrowIfNull(candidates);
        if (answer.TrimStart().StartsWith("INSUFFICIENT_EVIDENCE", StringComparison.OrdinalIgnoreCase))
        {
            return new CitationValidationResult(false, "已检索当前文档，但没有足够证据回答该问题。", []);
        }

        var known = candidates.ToDictionary(candidate => candidate.CitationId, StringComparer.Ordinal);
        var ids = CitationRegex().Matches(answer)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return new CitationValidationResult(false, "AI 回答未引用任何可反查证据，已拒绝展示为有据答案。", []);
        }

        var invalid = ids.Where(id => !known.ContainsKey(id)).ToArray();
        if (invalid.Length > 0)
        {
            return new CitationValidationResult(false, $"AI 返回了不存在的证据标识 {string.Join(", ", invalid)}，已拒绝该答案。", []);
        }

        if (PageReferenceRegex().IsMatch(CitationRegex().Replace(answer, string.Empty)))
        {
            return new CitationValidationResult(false, "AI 绕过本地证据标识自行声称了页码，已拒绝该答案。", []);
        }

        var uncited = SentenceRegex().Matches(answer)
            .Select(match => match.Value.Trim())
            .Where(IsSubstantiveSentence)
            .FirstOrDefault(sentence => !CitationRegex().IsMatch(sentence));
        if (uncited is not null)
        {
            return new CitationValidationResult(false, "AI 回答包含未附本地证据标识的事实性句子，已拒绝该答案。", []);
        }

        return new CitationValidationResult(
            true,
            "引用已通过本地反查。",
            ids.Select(id => known[id]).ToArray());
    }

    [GeneratedRegex("\\[(E[0-9]+)\\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();

    [GeneratedRegex("(?i)\\bpage\\s*\\d+\\b|\\bp\\.\\s*\\d+\\b|第\\s*\\d+\\s*页", RegexOptions.CultureInvariant)]
    private static partial Regex PageReferenceRegex();

    [GeneratedRegex("[^\r\n。！？!?]+[。！？!?]?", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();

    private static bool IsSubstantiveSentence(string sentence)
    {
        var trimmed = sentence.TrimStart('-', '*', '#', ' ');
        if (trimmed.Length < 7 || trimmed.EndsWith(':') || trimmed.EndsWith('：'))
        {
            return false;
        }

        return trimmed.Any(character => char.IsLetterOrDigit(character) || character is >= '\u4e00' and <= '\u9fff');
    }
}
