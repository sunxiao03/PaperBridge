using PaperBridge.Application.Bilingual;
using PaperBridge.Application.Reading;
using PaperBridge.Infrastructure.Pdf;

namespace PaperBridge.Infrastructure.Tests.Pdf;

public sealed class PdfiumDocumentTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "pdfium-text-layer-sample.pdf");

    [Fact]
    public async Task Open_ExtractsExpectedTextAndCharacterCoordinates()
    {
        await using var document = PdfiumDocument.Open(FixturePath);

        var page = await document.ExtractPageTextAsync(0);

        Assert.Equal(2, document.PageCount);
        Assert.Equal(612, page.WidthInPoints, precision: 1);
        Assert.Equal(792, page.HeightInPoints, precision: 1);
        Assert.Contains("The effective multiplication factor is unity.", page.Text);
        Assert.Contains("Neutron flux remains stable", page.Text);
        Assert.NotEmpty(page.Characters);
        Assert.Contains(page.Characters, character =>
            character.Text == "N" && character.Bounds is { Width: > 0, Height: > 0 });
    }

    [Fact]
    public async Task ExtractedCoordinatesFeedLayoutAnalysisWithoutUnmappedContent()
    {
        await using var document = PdfiumDocument.Open(FixturePath);

        var page = await document.ExtractPageTextAsync(0);
        var analysis = PageLayoutAnalyzer.Analyze(page);

        Assert.NotEmpty(analysis.Paragraphs);
        Assert.InRange(analysis.Confidence, 0, 1);
        Assert.All(analysis.Paragraphs, paragraph => Assert.Equal(0, paragraph.PageIndex));
        if (analysis.RecommendedMode == BilingualLayoutMode.PageAligned)
        {
            Assert.False(string.IsNullOrWhiteSpace(analysis.DegradationReason));
        }
    }

    [Fact]
    public async Task ExtractedPagesBuildSearchableCurrentDocumentEvidenceCorpus()
    {
        await using var document = PdfiumDocument.Open(FixturePath);
        var outline = await document.GetOutlineAsync();

        var corpus = await DocumentCorpusBuilder.BuildAsync(document, outline);
        var evidence = EvidenceRetriever.Search(corpus, "neutron flux stable");

        Assert.Equal(document.PageCount, corpus.PageCount);
        Assert.NotEmpty(corpus.Chunks);
        Assert.Contains(evidence, item =>
            item.PageIndex == 0 && item.EnglishExcerpt.Contains("Neutron flux", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractPageTextAsync_RejectsOutOfRangePage()
    {
        await using var document = PdfiumDocument.Open(FixturePath);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await document.ExtractPageTextAsync(document.PageCount));
    }

    [Fact]
    public async Task ExtractPageTextAsync_ObservesPreCancelledToken()
    {
        await using var document = PdfiumDocument.Open(FixturePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await document.ExtractPageTextAsync(0, cancellation.Token));
    }

    [Fact]
    public async Task RepeatedOpenExtractDispose_CompletesWithoutNativeHandleFailure()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            await using var document = PdfiumDocument.Open(FixturePath);
            var page = await document.ExtractPageTextAsync(iteration % document.PageCount);
            Assert.NotEmpty(page.Text);
        }
    }

    [Fact]
    public async Task MultipleOpenDocuments_RenderAndExtractBeforeSharedCleanup()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var documents = Enumerable.Range(0, 5)
                .Select(_ => PdfiumDocument.Open(FixturePath))
                .ToArray();

            try
            {
                await Task.WhenAll(documents.Select((document, index) => Task.Run(async () =>
                {
                    var pageIndex = index % document.PageCount;
                    var text = await document.ExtractPageTextAsync(pageIndex);
                    var rendered = await document.RenderPageAsync(pageIndex, new(scale: 0.5));

                    Assert.NotEmpty(text.Text);
                    Assert.True(rendered.ByteSize > 0);
                })));
            }
            finally
            {
                foreach (var document in documents)
                {
                    await document.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    public async Task RenderPageAsync_ReturnsBoundedBgraPixelBuffer()
    {
        await using var document = PdfiumDocument.Open(FixturePath);

        var rendered = await document.RenderPageAsync(0, new(scale: 1));

        Assert.Equal(612, rendered.PixelWidth);
        Assert.Equal(792, rendered.PixelHeight);
        Assert.True(rendered.Stride >= rendered.PixelWidth * 4);
        Assert.Equal((long)rendered.Stride * rendered.PixelHeight, rendered.ByteSize);
        Assert.Contains(rendered.Bgra32Pixels, component => component < 240);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsEmbeddedDocumentInformation()
    {
        await using var document = PdfiumDocument.Open(FixturePath);

        var metadata = await document.GetMetadataAsync();

        Assert.Equal("PDFium Text Layer Fixture", metadata.Title);
        Assert.Equal("PaperBridge contributors", metadata.Author);
        Assert.Equal("Public-domain integration test fixture", metadata.Subject);
        Assert.Contains("reactor physics", metadata.Keywords, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PaperBridge fixture generator", metadata.Creator);
        Assert.StartsWith("D:2026", metadata.CreationDate);
    }

    [Fact]
    public async Task GetOutlineAsync_ReturnsHierarchyAndPageDestinations()
    {
        await using var document = PdfiumDocument.Open(FixturePath);

        var outline = await document.GetOutlineAsync();

        Assert.Equal(2, outline.Count);
        Assert.Equal("Reactor physics text", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
        var child = Assert.Single(outline[0].Children);
        Assert.Equal("Reference parameters", child.Title);
        Assert.Equal(0, child.PageIndex);
        Assert.Equal("Two-column extraction fixture", outline[1].Title);
        Assert.Equal(1, outline[1].PageIndex);
    }
}
