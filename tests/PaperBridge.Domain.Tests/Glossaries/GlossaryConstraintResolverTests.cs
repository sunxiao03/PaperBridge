using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Glossaries;
using PaperBridge.Domain.Glossaries;

namespace PaperBridge.Domain.Tests.Glossaries;

public sealed class GlossaryConstraintResolverTests
{
    [Fact]
    public void Resolve_UsesApprovedUserOverrideAndOnlyRelevantTerms()
    {
        var builtIn = new GlossaryDefinition(Guid.NewGuid(), "built-in", GlossarySource.BuiltIn);
        var personal = new GlossaryDefinition(Guid.NewGuid(), "personal", GlossarySource.User);
        var terms = new[]
        {
            Term(builtIn, "neutron flux", "内置译名"),
            Term(personal, "neutron flux", "中子通量"),
            Term(builtIn, "reactivity", "反应性"),
            Term(personal, "burnup", "不应使用", GlossaryReviewStatus.Pending)
        };

        var result = GlossaryConstraintResolver.Resolve(
            new GlossarySnapshot([builtIn, personal], terms),
            "The neutron flux is measured in the core.");

        Assert.Equal(new Dictionary<string, string> { ["neutron flux"] = "中子通量" }, result.Terminology);
        Assert.Equal(64, result.Version.Length);

        var changed = GlossaryConstraintResolver.Resolve(
            new GlossarySnapshot(
                [builtIn, personal],
                terms.Select(term => term.Source == GlossarySource.User && term.English == "neutron flux"
                    ? new GlossaryTerm(
                        term.English, "中子注量率", term.Source, term.Priority, term.Category,
                        term.Explanation, term.SourceReference, term.Id, term.GlossaryId,
                        term.EnglishAliases, term.ChineseAliases, term.Notes, term.ReviewStatus,
                        term.UpdatedAtUtc.AddSeconds(1))
                    : term).ToArray()),
            "The neutron flux is measured in the core.");
        Assert.NotEqual(result.Version, changed.Version);
    }

    [Fact]
    public void Resolve_MatchesAliasButDoesNotMatchInsideAnotherWord()
    {
        var glossary = new GlossaryDefinition(Guid.NewGuid(), "g", GlossarySource.User);
        var term = new GlossaryTerm(
            "effective multiplication factor",
            "有效增殖因数",
            GlossarySource.User,
            glossaryId: glossary.Id,
            englishAliases: ["k-effective"]);
        var snapshot = new GlossarySnapshot([glossary], [term]);

        Assert.Single(GlossaryConstraintResolver.Resolve(snapshot, "The k-effective is unity.").Terminology);
        Assert.Empty(GlossaryConstraintResolver.Resolve(snapshot, "A fluxional expression.").Terminology);
    }

    [Fact]
    public void Resolve_DisabledGlossaryProducesNoConstraints()
    {
        var glossary = new GlossaryDefinition(Guid.NewGuid(), "g", GlossarySource.BuiltIn, isEnabled: false);
        var snapshot = new GlossarySnapshot([glossary], [Term(glossary, "criticality", "临界状态")]);

        var result = GlossaryConstraintResolver.Resolve(snapshot, "criticality safety");

        Assert.Empty(result.Terminology);
        Assert.Equal("none-v1", result.Version);
    }

    private static GlossaryTerm Term(
        GlossaryDefinition glossary,
        string english,
        string chinese,
        GlossaryReviewStatus status = GlossaryReviewStatus.Approved) =>
        new(english, chinese, glossary.Source, glossaryId: glossary.Id, reviewStatus: status);
}
