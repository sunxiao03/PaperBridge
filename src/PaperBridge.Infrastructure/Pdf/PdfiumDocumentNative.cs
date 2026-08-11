using System.Runtime.InteropServices;

namespace PaperBridge.Infrastructure.Pdf;

internal static class PdfiumDocumentNative
{
    private const string LibraryName = "pdfium";

    [DllImport(LibraryName, EntryPoint = "FPDF_GetMetaText", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong FPDF_GetMetaText(
        IntPtr document,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string tag,
        IntPtr buffer,
        ulong bufferLength);

    [DllImport(LibraryName, EntryPoint = "FPDFBookmark_GetFirstChild", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [DllImport(LibraryName, EntryPoint = "FPDFBookmark_GetNextSibling", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    [DllImport(LibraryName, EntryPoint = "FPDFBookmark_GetTitle", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, ulong bufferLength);

    [DllImport(LibraryName, EntryPoint = "FPDFBookmark_GetDest", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [DllImport(LibraryName, EntryPoint = "FPDFDest_GetDestPageIndex", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr destination);
}
