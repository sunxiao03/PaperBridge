using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Reading;

namespace PaperBridge.Domain.Tests.Reading;

public sealed class ReadingAssistantCoreTests
{
    [Theory]
    [InlineData(-120, 72)]
    [InlineData(120, -72)]
    [InlineData(-12000, 72)]
    [InlineData(12000, -72)]
    public void PdfWheelMovementIsPixelBasedAndCapsExtremeDeviceDeltas(int delta, double expected)
    {
        Assert.Equal(expected, PdfScrollWheel.GetPixelMovement(delta, 800), precision: 3);
    }

    [Fact]
    public void PdfWheelMovementPreservesSmallPrecisionTrackpadDeltas()
    {
        var movement = PdfScrollWheel.GetPixelMovement(-12, 800);

        Assert.InRange(movement, 7.19, 7.21);
        Assert.True(movement < PdfScrollWheel.MaximumStepPixels);
    }

    [Fact]
    public void CorpusUsesOutlineSectionsAndBoundedPageChunks()
    {
        var pages = new[]
        {
            Page(0, "Introduction to neutron transport and criticality."),
            Page(1, "The transport equation is solved with a discrete ordinates method."),
            Page(2, "Results show stable neutron flux under the stated boundary conditions.")
        };
        var outline = new[]
        {
            new PdfOutlineItem("Introduction", 0, []),
            new PdfOutlineItem("Results", 2, [])
        };

        var corpus = DocumentCorpusBuilder.Build(pages, outline);

        Assert.Equal(3, corpus.PageCount);
        Assert.Equal(2, corpus.Sections.Count);
        Assert.Contains(corpus.Chunks, chunk => chunk.PageIndex == 1 && chunk.SectionTitle == "Introduction");
        Assert.Contains(corpus.Chunks, chunk => chunk.PageIndex == 2 && chunk.SectionTitle == "Results");
        Assert.All(corpus.Chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 3_000));
    }

    [Fact]
    public void CorpusWithoutOutlineDegradesToExplicitPageSections()
    {
        var corpus = DocumentCorpusBuilder.Build([Page(0, "Page text")], []);

        var section = Assert.Single(corpus.Sections);
        Assert.Contains("PDF 无目录", section.Title, StringComparison.Ordinal);
        Assert.Equal(section.Title, Assert.Single(corpus.Chunks).SectionTitle);
    }

    [Fact]
    public void CurrentSectionFollowsReadingPageAndReportsItsExactRange()
    {
        var outline = new[]
        {
            new PdfOutlineItem("Introduction", 0, []),
            new PdfOutlineItem("Methods", 2, []),
            new PdfOutlineItem("Results", 5, [])
        };

        var section = DocumentCorpusBuilder.ResolveSection(8, outline, 3);

        Assert.Equal("Methods", section.Title);
        Assert.Equal(2, section.StartPageIndex);
        Assert.Equal(5, section.EndPageIndexExclusive);
    }

    [Fact]
    public void CurrentSectionWithoutOutlineExplicitlyFallsBackToCurrentPage()
    {
        var section = DocumentCorpusBuilder.ResolveSection(8, [], 3);

        Assert.Equal("第 4 页（PDF 无目录）", section.Title);
        Assert.Equal(3, section.StartPageIndex);
        Assert.Equal(4, section.EndPageIndexExclusive);
    }

    [Fact]
    public void EvidenceSearchReturnsOnlyLocalExactExcerptsWithStablePageMapping()
    {
        var corpus = DocumentCorpusBuilder.Build(
            [
                Page(0, "A generic introduction to reactor analysis."),
                Page(1, "Neutron flux remains stable when the multiplication factor is unity.")
            ],
            [new PdfOutlineItem("Methods", 0, []), new PdfOutlineItem("Results", 1, [])]);

        var evidence = EvidenceRetriever.Search(corpus, "What happens to neutron flux?");

        var item = Assert.Single(evidence);
        Assert.Equal("E1", item.CitationId);
        Assert.Equal(1, item.PageIndex);
        Assert.Equal("Results", item.SectionTitle);
        Assert.Contains("Neutron flux", item.EnglishExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void CitationValidationRejectsMissingInventedAndDirectPageReferences()
    {
        var evidence = new[]
        {
            new EvidenceCandidate("E1", "chunk", 3, "Results", "Flux is stable.", 2)
        };

        Assert.False(CitationValidator.Validate("通量稳定。", evidence).IsValid);
        Assert.False(CitationValidator.Validate("通量稳定 [E9]。", evidence).IsValid);
        Assert.False(CitationValidator.Validate("通量稳定 [E0] [E1]。", evidence).IsValid);
        Assert.False(CitationValidator.Validate("第 4 页表明通量稳定 [E1]。", evidence).IsValid);
        Assert.False(CitationValidator.Validate("通量稳定 [E1]。这证明了系统安全。", evidence).IsValid);

        var valid = CitationValidator.Validate("在给定条件下通量稳定 [E1]。", evidence);
        Assert.True(valid.IsValid);
        Assert.Equal(evidence, valid.CitedEvidence);
    }

    [Fact]
    public void TextBundlerNeverExceedsConfiguredRequestSize()
    {
        var chunks = Enumerable.Range(0, 10).Select(index => new DocumentTextChunk(
            $"c{index}", index, "Section", 0, new string((char)('a' + index), 700))).ToArray();

        var bundles = ReadingTextBundler.Bundle(chunks, 2_000);

        Assert.True(bundles.Count > 1);
        Assert.All(bundles, bundle => Assert.InRange(bundle.Length, 1, 2_000));
    }

    [Fact]
    public async Task CoordinatorUsesVersionedCacheForDeterministicTasks()
    {
        var provider = new FakeProvider();
        var cache = new MemoryCache();
        using var coordinator = new ReadingAssistantCoordinator(provider, cache, maximumConcurrency: 1);
        var job = new ReadingAssistantJob(
            new string('a', 64), ReadingTaskKind.SectionSummary, "model", "system", "user", "none");

        var first = await coordinator.CompleteAsync(job);
        var second = await coordinator.CompleteAsync(job);

        Assert.False(first.IsCacheHit);
        Assert.True(second.IsCacheHit);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task DisposingCoordinatorCancelsActiveRequestWithoutSemaphoreRace()
    {
        var provider = new BlockingProvider();
        var coordinator = new ReadingAssistantCoordinator(provider, new MemoryCache(), maximumConcurrency: 1);
        var job = new ReadingAssistantJob(
            new string('c', 64), ReadingTaskKind.DocumentSynthesis, "model", "system", "user", "none", Cacheable: false);

        var pending = coordinator.CompleteAsync(job);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static PdfPageText Page(int index, string text)
    {
        var characters = text.Select((character, sourceIndex) => new PdfTextCharacter(
            sourceIndex,
            character.ToString(),
            character == ' ' ? null : new PdfRectangle(20 + (sourceIndex * 4), 700, 23 + (sourceIndex * 4), 710)))
            .ToArray();
        return new PdfPageText(index, 612, 792, text, characters, []);
    }

    private sealed class FakeProvider : IReadingAssistantProvider
    {
        public string ProviderId => "fake";

        public int CallCount { get; private set; }

        public Task<ReadingAssistantResponse> CompleteAsync(
            ReadingAssistantRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ReadingAssistantResponse("summary", request.Model, 10, 5));
        }
    }

    private sealed class MemoryCache : IReadingAssistantCache
    {
        private readonly Dictionary<string, CachedReadingAssistantResult> _values = [];

        public Task<CachedReadingAssistantResult?> GetAsync(
            ReadingAssistantCacheKey key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key.ToStableId()));

        public Task SetAsync(
            ReadingAssistantCacheKey key,
            CachedReadingAssistantResult result,
            CancellationToken cancellationToken = default)
        {
            _values[key.ToStableId()] = result;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingProvider : IReadingAssistantProvider
    {
        public string ProviderId => "blocking";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReadingAssistantResponse> CompleteAsync(
            ReadingAssistantRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after infinite delay.");
        }
    }
}
