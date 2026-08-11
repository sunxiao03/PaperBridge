using System.Text.RegularExpressions;

namespace PaperBridge.Application.Reading;

public static partial class EvidenceRetriever
{
    public const int MaximumEvidenceItems = 8;
    public const int MaximumEvidenceCharacters = 24_000;
    public const int MaximumExcerptCharacters = 1_200;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "that", "with", "from", "this", "what", "which", "when", "where", "who",
        "how", "why", "are", "was", "were", "have", "has", "had", "into", "about", "paper", "study", "to"
    };

    public static IReadOnlyList<EvidenceCandidate> Search(
        DocumentCorpus corpus,
        string question,
        string? expandedEnglishTerms = null)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var combined = question + " " + expandedEnglishTerms;
        var terms = WordRegex().Matches(combined.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(term => term.Length >= 2 && !StopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        if (terms.Length == 0 || corpus.Chunks.Count == 0)
        {
            return [];
        }

        var phrase = string.Join(' ', WordRegex().Matches(question.ToLowerInvariant()).Select(match => match.Value));
        var indexed = corpus.Chunks.Select(chunk =>
            {
                var text = chunk.Text.ToLowerInvariant();
                var tokens = WordRegex().Matches(text)
                    .Select(match => match.Value)
                    .ToArray();
                var frequencies = tokens
                    .GroupBy(value => value, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var sectionTerms = WordRegex().Matches(chunk.SectionTitle.ToLowerInvariant())
                    .Select(match => match.Value)
                    .ToHashSet(StringComparer.Ordinal);
                return new IndexedChunk(chunk, text, tokens.Length, frequencies, sectionTerms);
            })
            .ToArray();
        var averageLength = Math.Max(1, indexed.Average(item => item.TokenCount));
        var documentFrequency = terms.ToDictionary(
            term => term,
            term => indexed.Count(item => item.Frequencies.ContainsKey(term)),
            StringComparer.Ordinal);
        const double k1 = 1.2;
        const double b = 0.75;
        var ranked = indexed.Select(item =>
            {
                var score = 0d;
                foreach (var term in terms)
                {
                    var frequency = item.Frequencies.GetValueOrDefault(term);
                    var frequencyInDocuments = documentFrequency[term];
                    if (frequency > 0 && frequencyInDocuments > 0)
                    {
                        var inverseDocumentFrequency = Math.Log(
                            1 + ((indexed.Length - frequencyInDocuments + 0.5) / (frequencyInDocuments + 0.5)));
                        var normalization = frequency + (k1 * (1 - b + (b * item.TokenCount / averageLength)));
                        score += inverseDocumentFrequency * (frequency * (k1 + 1)) / normalization;
                        if (item.SectionTerms.Contains(term))
                        {
                            score += inverseDocumentFrequency * 0.5;
                        }
                    }
                }

                if (phrase.Length >= 8 && item.NormalizedText.Contains(phrase, StringComparison.Ordinal))
                {
                    score += 3;
                }

                return (Chunk: item.Chunk, Score: score);
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.PageIndex)
            .ThenBy(item => item.Chunk.SourceStart)
            .ToArray();

        var result = new List<EvidenceCandidate>();
        var characters = 0;
        foreach (var item in ranked)
        {
            var excerpt = item.Chunk.Text.Length <= MaximumExcerptCharacters
                ? item.Chunk.Text
                : item.Chunk.Text[..MaximumExcerptCharacters].TrimEnd() + "…";
            if (result.Count >= MaximumEvidenceItems || characters + excerpt.Length > MaximumEvidenceCharacters)
            {
                break;
            }

            result.Add(new EvidenceCandidate(
                $"E{result.Count + 1}",
                item.Chunk.StableId,
                item.Chunk.PageIndex,
                item.Chunk.SectionTitle,
                excerpt,
                item.Score));
            characters += excerpt.Length;
        }

        return result;
    }

    [GeneratedRegex("[a-z][a-z0-9_-]*|[0-9]+(?:\\.[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    private sealed record IndexedChunk(
        DocumentTextChunk Chunk,
        string NormalizedText,
        int TokenCount,
        IReadOnlyDictionary<string, int> Frequencies,
        IReadOnlySet<string> SectionTerms);
}
