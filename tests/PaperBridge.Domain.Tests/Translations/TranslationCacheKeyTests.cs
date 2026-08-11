using PaperBridge.Domain.Translations;

namespace PaperBridge.Domain.Tests.Translations;

public sealed class TranslationCacheKeyTests
{
    [Fact]
    public void ToStableId_SameInputsProduceSameIdentifier()
    {
        var first = Create("g1");
        var second = Create("g1");

        Assert.Equal(first.ToStableId(), second.ToStableId());
        Assert.Equal(64, first.ToStableId().Length);
    }

    [Fact]
    public void ToStableId_GlossaryVersionChangesIdentifier()
    {
        var first = Create("g1");
        var second = Create("g2");

        Assert.NotEqual(first.ToStableId(), second.ToStableId());
    }

    [Fact]
    public void ToStableId_GranularityAndCustomInstructionVersionChangeIdentifier()
    {
        var baseline = Create("g1");
        var page = new TranslationCacheKey(
            "document-sha256",
            "The effective multiplication factor is unity.",
            "OpenAI",
            "configured-model",
            "p1",
            "g1",
            TranslationGranularity.Page);
        var custom = new TranslationCacheKey(
            "document-sha256",
            "The effective multiplication factor is unity.",
            "OpenAI",
            "configured-model",
            "p1",
            "g1",
            customInstructionVersion: "custom-v2");

        Assert.NotEqual(baseline.ToStableId(), page.ToStableId());
        Assert.NotEqual(baseline.ToStableId(), custom.ToStableId());
    }

    private static TranslationCacheKey Create(string glossaryVersion) =>
        new(
            documentHash: "document-sha256",
            sourceText: "The effective multiplication factor is unity.",
            provider: "OpenAI",
            model: "configured-model",
            promptVersion: "p1",
            glossaryVersion: glossaryVersion);
}
