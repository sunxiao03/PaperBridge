using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PaperBridge.Application.Abstractions;
using PaperBridge.Domain.Glossaries;

namespace PaperBridge.Application.Glossaries;

public static partial class GlossaryConstraintResolver
{
    public const int MaximumConstraints = 64;

    public static GlossaryConstraints Resolve(GlossarySnapshot snapshot, params string?[] texts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var searchable = string.Join('\n', texts.Where(text => !string.IsNullOrWhiteSpace(text))).ToLowerInvariant();
        if (searchable.Length == 0)
        {
            return GlossaryConstraints.Empty;
        }

        var resolved = GlossaryResolver.Resolve(snapshot.Terms, snapshot.Glossaries);
        var matches = resolved.Values
            .Select(term => new
            {
                Term = term,
                MatchLength = GetMatchedLength(term, searchable)
            })
            .Where(match => match.MatchLength > 0)
            .OrderByDescending(match => match.MatchLength)
            .ThenByDescending(match => match.Term.Source)
            .ThenByDescending(match => match.Term.Priority)
            .ThenBy(match => match.Term.English, StringComparer.Ordinal)
            .Take(MaximumConstraints)
            .Select(match => match.Term)
            .ToArray();

        if (matches.Length == 0)
        {
            return GlossaryConstraints.Empty;
        }

        var terminology = matches.ToDictionary(
            term => term.English,
            term => term.PreferredChinese,
            StringComparer.OrdinalIgnoreCase);
        var versionMaterial = string.Join('\n', matches
            .OrderBy(term => term.English, StringComparer.Ordinal)
            .Select(term => $"{term.Id:N}|{term.English}|{term.PreferredChinese}|{term.UpdatedAtUtc:O}"));
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(versionMaterial)));
        return new GlossaryConstraints(terminology, version);
    }

    private static int GetMatchedLength(GlossaryTerm term, string searchable)
    {
        var candidates = term.EnglishAliases.Prepend(term.English);
        return candidates
            .Where(candidate => ContainsEnglishPhrase(searchable, candidate))
            .Select(candidate => candidate.Length)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static bool ContainsEnglishPhrase(string text, string phrase)
    {
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}

public sealed record GlossaryConstraints(
    IReadOnlyDictionary<string, string> Terminology,
    string Version)
{
    public static GlossaryConstraints Empty { get; } = new(
        new Dictionary<string, string>(),
        "none-v1");
}
