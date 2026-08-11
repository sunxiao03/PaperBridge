namespace PaperBridge.Application.Abstractions;

public interface IPdfDocument : IAsyncDisposable
{
    int PageCount { get; }

    ValueTask<PdfPageText> ExtractPageTextAsync(int pageIndex, CancellationToken cancellationToken = default);

    ValueTask<PdfDocumentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(CancellationToken cancellationToken = default);

    ValueTask<PdfRenderedPage> RenderPageAsync(
        int pageIndex,
        PdfRenderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PdfPageText(
    int PageIndex,
    double WidthInPoints,
    double HeightInPoints,
    string Text,
    IReadOnlyList<PdfTextCharacter> Characters,
    IReadOnlyList<PdfTextBlock> Blocks);

public sealed record PdfTextCharacter(int SourceIndex, string Text, PdfRectangle? Bounds);

public sealed record PdfTextBlock(string Text, double Left, double Top, double Width, double Height, double Confidence);

public sealed record PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;
}

public sealed record PdfRenderRequest
{
    public PdfRenderRequest(double scale)
    {
        if (!double.IsFinite(scale) || scale is < 0.1 or > 8.0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be between 0.1 and 8.0.");
        }

        Scale = scale;
    }

    public double Scale { get; }
}

public sealed record PdfRenderedPage(
    int PageIndex,
    int PixelWidth,
    int PixelHeight,
    int Stride,
    byte[] Bgra32Pixels)
{
    public long ByteSize => Bgra32Pixels.LongLength;
}

public sealed record PdfDocumentMetadata(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer,
    string? CreationDate,
    string? ModificationDate);

public sealed record PdfOutlineItem(
    string Title,
    int? PageIndex,
    IReadOnlyList<PdfOutlineItem> Children);
