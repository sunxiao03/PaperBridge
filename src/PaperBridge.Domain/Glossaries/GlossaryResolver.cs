namespace PaperBridge.Domain.Glossaries;

public static class GlossaryResolver
{
    public static IReadOnlyDictionary<string, GlossaryTerm> Resolve(
        IEnumerable<GlossaryTerm> terms,
        IEnumerable<GlossaryDefinition>? glossaries = null)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var byId = glossaries?.ToDictionary(glossary => glossary.Id) ?? [];
        return terms
            .Where(term => term.ReviewStatus == GlossaryReviewStatus.Approved)
            .Where(term => term.GlossaryId == Guid.Empty ||
                !byId.TryGetValue(term.GlossaryId, out var glossary) || glossary.IsEnabled)
            .GroupBy(term => term.English, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(term => term.Source)
                    .ThenByDescending(term => byId.TryGetValue(term.GlossaryId, out var glossary)
                        ? glossary.Priority
                        : 0)
                    .ThenByDescending(term => term.Priority)
                    .ThenByDescending(term => term.UpdatedAtUtc)
                    .ThenBy(term => term.Id)
                    .First(),
                StringComparer.Ordinal);
    }
}
