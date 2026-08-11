using System.Runtime.InteropServices;
using System.Text;
using PaperBridge.Application.Abstractions;
using PDFiumCore;

namespace PaperBridge.Infrastructure.Pdf;

public sealed class PdfiumDocument : IPdfDocument, IDisposable
{
    private const long MaximumRenderedPageBytes = 128L * 1024 * 1024;
    private const int MaximumOutlineDepth = 64;
    private const int MaximumOutlineItems = 10_000;
    private readonly IDisposable _libraryLease;
    private FpdfDocumentT? _document;
    private int _disposed;

    private PdfiumDocument(FpdfDocumentT document, IDisposable libraryLease)
    {
        _document = document;
        _libraryLease = libraryLease;
        PageCount = fpdfview.FPDF_GetPageCount(document);
    }

    ~PdfiumDocument()
    {
        Dispose(disposing: false);
    }

    public int PageCount { get; }

    public static PdfiumDocument Open(string filePath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The PDF file was not found.", fullPath);
        }

        lock (PdfiumLibraryLifetime.SyncRoot)
        {
            var libraryLease = PdfiumLibraryLifetime.Acquire();
            try
            {
                var document = fpdfview.FPDF_LoadDocument(fullPath, password ?? string.Empty);
                if (IsNull(document))
                {
                    var error = (PdfiumError)fpdfview.FPDF_GetLastError();
                    throw new PdfiumException($"PDFium could not open '{Path.GetFileName(fullPath)}'.", error);
                }

                return new PdfiumDocument(document, libraryLease);
            }
            catch
            {
                libraryLease.Dispose();
                throw;
            }
        }
    }

    public ValueTask<PdfPageText> ExtractPageTextAsync(
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        lock (PdfiumLibraryLifetime.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();

            var document = _document!;
            var page = fpdfview.FPDF_LoadPage(document, pageIndex);
            if (IsNull(page))
            {
                throw new PdfiumException($"PDFium could not load page {pageIndex + 1}.", PdfiumError.Page);
            }

            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (IsNull(textPage))
                {
                    throw new PdfiumException(
                        $"PDFium could not initialize text extraction for page {pageIndex + 1}.",
                        PdfiumError.Page);
                }

                try
                {
                    return ValueTask.FromResult(ExtractText(pageIndex, page, textPage, cancellationToken));
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public ValueTask<PdfRenderedPage> RenderPageAsync(
        int pageIndex,
        PdfRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        lock (PdfiumLibraryLifetime.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();

            var page = fpdfview.FPDF_LoadPage(_document!, pageIndex);
            if (IsNull(page))
            {
                throw new PdfiumException($"PDFium could not load page {pageIndex + 1}.", PdfiumError.Page);
            }

            try
            {
                var pixelWidth = ToPixelDimension(fpdfview.FPDF_GetPageWidthF(page), request.Scale);
                var pixelHeight = ToPixelDimension(fpdfview.FPDF_GetPageHeightF(page), request.Scale);
                EnsureRenderSize(pixelWidth, pixelHeight);

                var bitmap = fpdfview.FPDFBitmapCreate(pixelWidth, pixelHeight, 1);
                if (IsNull(bitmap))
                {
                    throw new PdfiumException("PDFium could not allocate the page bitmap.", PdfiumError.Unknown);
                }

                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                    fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, 0);
                    cancellationToken.ThrowIfCancellationRequested();

                    var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                    var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                    var byteCount = checked(stride * pixelHeight);
                    if (buffer == IntPtr.Zero || byteCount <= 0 || byteCount > MaximumRenderedPageBytes)
                    {
                        throw new PdfiumException("PDFium returned an invalid page bitmap.", PdfiumError.Unknown);
                    }

                    var pixels = new byte[byteCount];
                    Marshal.Copy(buffer, pixels, 0, byteCount);

                    return ValueTask.FromResult(
                        new PdfRenderedPage(pageIndex, pixelWidth, pixelHeight, stride, pixels));
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public ValueTask<PdfDocumentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (PdfiumLibraryLifetime.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var document = _document!.__Instance;
            return ValueTask.FromResult(
                new PdfDocumentMetadata(
                    ReadMetadata(document, "Title"),
                    ReadMetadata(document, "Author"),
                    ReadMetadata(document, "Subject"),
                    ReadMetadata(document, "Keywords"),
                    ReadMetadata(document, "Creator"),
                    ReadMetadata(document, "Producer"),
                    ReadMetadata(document, "CreationDate"),
                    ReadMetadata(document, "ModDate")));
        }
    }

    public ValueTask<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (PdfiumLibraryLifetime.SyncRoot)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var visited = new HashSet<IntPtr>();
            var itemCount = 0;
            IReadOnlyList<PdfOutlineItem> outline = ReadOutlineChildren(
                _document!.__Instance,
                IntPtr.Zero,
                depth: 0,
                visited,
                ref itemCount,
                cancellationToken);
            return ValueTask.FromResult(outline);
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static PdfPageText ExtractText(
        int pageIndex,
        FpdfPageT page,
        FpdfTextpageT textPage,
        CancellationToken cancellationToken)
    {
        var characterCount = fpdf_text.FPDFTextCountChars(textPage);
        if (characterCount < 0)
        {
            throw new PdfiumException(
                $"PDFium could not count characters on page {pageIndex + 1}.",
                PdfiumError.Page);
        }

        var text = new StringBuilder(characterCount);
        var characters = new List<PdfTextCharacter>(characterCount);

        for (var index = 0; index < characterCount; index++)
        {
            if ((index & 127) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var codePoint = fpdf_text.FPDFTextGetUnicode(textPage, index);
            if (codePoint == 0 || codePoint > 0x10FFFF)
            {
                continue;
            }

            var value = char.ConvertFromUtf32((int)codePoint);
            text.Append(value);
            characters.Add(new PdfTextCharacter(index, value, TryGetBounds(textPage, index)));
        }

        return new PdfPageText(
            pageIndex,
            fpdfview.FPDF_GetPageWidthF(page),
            fpdfview.FPDF_GetPageHeightF(page),
            text.ToString(),
            characters,
            Array.Empty<PdfTextBlock>());
    }

    private static PdfRectangle? TryGetBounds(FpdfTextpageT textPage, int characterIndex)
    {
        var left = 0d;
        var right = 0d;
        var bottom = 0d;
        var top = 0d;

        if (fpdf_text.FPDFTextGetCharBox(textPage, characterIndex, ref left, ref right, ref bottom, ref top) == 0)
        {
            return null;
        }

        return new PdfRectangle(
            Math.Min(left, right),
            Math.Min(bottom, top),
            Math.Max(left, right),
            Math.Max(bottom, top));
    }

    private static string? ReadMetadata(IntPtr document, string tag)
    {
        var value = ReadPdfiumUtf16String(
            (buffer, length) => PdfiumDocumentNative.FPDF_GetMetaText(document, tag, buffer, length));
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<PdfOutlineItem> ReadOutlineChildren(
        IntPtr document,
        IntPtr parent,
        int depth,
        HashSet<IntPtr> visited,
        ref int itemCount,
        CancellationToken cancellationToken)
    {
        if (depth >= MaximumOutlineDepth || itemCount >= MaximumOutlineItems)
        {
            return Array.Empty<PdfOutlineItem>();
        }

        var items = new List<PdfOutlineItem>();
        var bookmark = PdfiumDocumentNative.FPDFBookmark_GetFirstChild(document, parent);

        while (bookmark != IntPtr.Zero && itemCount < MaximumOutlineItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(bookmark))
            {
                break;
            }

            itemCount++;
            var title = ReadPdfiumUtf16String(
                (buffer, length) => PdfiumDocumentNative.FPDFBookmark_GetTitle(bookmark, buffer, length));
            var destination = PdfiumDocumentNative.FPDFBookmark_GetDest(document, bookmark);
            int? pageIndex = destination == IntPtr.Zero
                ? null
                : PdfiumDocumentNative.FPDFDest_GetDestPageIndex(document, destination);
            if (pageIndex < 0)
            {
                pageIndex = null;
            }

            var children = ReadOutlineChildren(
                document,
                bookmark,
                depth + 1,
                visited,
                ref itemCount,
                cancellationToken);
            items.Add(new PdfOutlineItem(
                string.IsNullOrWhiteSpace(title) ? "未命名目录项" : title.Trim(),
                pageIndex,
                children));

            bookmark = PdfiumDocumentNative.FPDFBookmark_GetNextSibling(document, bookmark);
        }

        return items;
    }

    private static string ReadPdfiumUtf16String(Func<IntPtr, ulong, ulong> read)
    {
        var requiredBytes = read(IntPtr.Zero, 0);
        if (requiredBytes <= 2)
        {
            return string.Empty;
        }

        if (requiredBytes > 1024 * 1024 || requiredBytes > int.MaxValue)
        {
            throw new PdfiumException("PDFium returned an invalid text buffer size.", PdfiumError.Format);
        }

        var buffer = Marshal.AllocHGlobal((int)requiredBytes);
        try
        {
            var writtenBytes = read(buffer, requiredBytes);
            if (writtenBytes <= 2 || writtenBytes > requiredBytes)
            {
                return string.Empty;
            }

            var bytes = new byte[checked((int)writtenBytes - 2)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsNull(FpdfDocumentT? handle) => handle is null || handle.__Instance == IntPtr.Zero;

    private static bool IsNull(FpdfPageT? handle) => handle is null || handle.__Instance == IntPtr.Zero;

    private static bool IsNull(FpdfTextpageT? handle) => handle is null || handle.__Instance == IntPtr.Zero;

    private static bool IsNull(FpdfBitmapT? handle) => handle is null || handle.__Instance == IntPtr.Zero;

    private static int ToPixelDimension(float points, double scale)
    {
        if (!float.IsFinite(points) || points <= 0)
        {
            throw new PdfiumException("PDFium returned an invalid page dimension.", PdfiumError.Page);
        }

        return checked((int)Math.Ceiling(points * scale));
    }

    private static void EnsureRenderSize(int pixelWidth, int pixelHeight)
    {
        var requiredBytes = checked((long)pixelWidth * pixelHeight * 4);
        if (requiredBytes > MaximumRenderedPageBytes)
        {
            throw new InvalidOperationException(
                $"The requested page bitmap requires {requiredBytes:N0} bytes; the per-page limit is {MaximumRenderedPageBytes:N0} bytes.");
        }
    }

    private void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (PdfiumLibraryLifetime.SyncRoot)
        {
            if (_document is not null && _document.__Instance != IntPtr.Zero)
            {
                try
                {
                    fpdfview.FPDF_CloseDocument(_document);
                }
                catch when (!disposing)
                {
                    // Finalizers must not surface native cleanup failures.
                }
                finally
                {
                    _document = null;
                }
            }

            _libraryLease.Dispose();
        }
    }
}
