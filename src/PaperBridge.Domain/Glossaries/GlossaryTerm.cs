namespace PaperBridge.Domain.Glossaries;

public sealed record GlossaryTerm
{
    public GlossaryTerm(
        string english,
        string preferredChinese,
        GlossarySource source,
        int priority = 0,
        string? category = null,
        string? explanation = null,
        string? sourceReference = null,
        Guid? id = null,
        Guid? glossaryId = null,
        IEnumerable<string>? englishAliases = null,
        IEnumerable<string>? chineseAliases = null,
        string? notes = null,
        GlossaryReviewStatus reviewStatus = GlossaryReviewStatus.Approved,
        DateTimeOffset? updatedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(english);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredChinese);
        if (english.Trim().Length > 256 || preferredChinese.Trim().Length > 256)
        {
            throw new ArgumentException("Glossary terms must not exceed 256 characters.");
        }

        English = NormalizeEnglish(english);
        PreferredChinese = preferredChinese.Trim();
        Source = source;
        Priority = priority;
        Category = Normalize(category);
        Explanation = Normalize(explanation);
        SourceReference = Normalize(sourceReference);
        Id = id ?? Guid.NewGuid();
        GlossaryId = glossaryId ?? Guid.Empty;
        EnglishAliases = NormalizeAliases(englishAliases, NormalizeEnglish);
        ChineseAliases = NormalizeAliases(chineseAliases, value => value.Trim());
        if (EnglishAliases.Count > 64 || ChineseAliases.Count > 64 ||
            EnglishAliases.Any(value => value.Length > 256) || ChineseAliases.Any(value => value.Length > 256))
        {
            throw new ArgumentException("A term can contain at most 64 aliases of up to 256 characters each.");
        }
        Notes = Normalize(notes);
        ReviewStatus = reviewStatus;
        UpdatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }

    public Guid GlossaryId { get; }

    public string English { get; }

    public string PreferredChinese { get; }

    public GlossarySource Source { get; }

    public int Priority { get; }

    public string? Category { get; }

    public string? Explanation { get; }

    public string? SourceReference { get; }

    public IReadOnlyList<string> EnglishAliases { get; }

    public IReadOnlyList<string> ChineseAliases { get; }

    public string? Notes { get; }

    public GlossaryReviewStatus ReviewStatus { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static string NormalizeEnglish(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeAliases(
        IEnumerable<string>? values,
        Func<string, string> normalize) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
}
