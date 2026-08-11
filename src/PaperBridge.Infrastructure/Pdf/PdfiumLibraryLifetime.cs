using PDFiumCore;

namespace PaperBridge.Infrastructure.Pdf;

internal static class PdfiumLibraryLifetime
{
    private static readonly object Gate = new();
    private static int _leaseCount;

    internal static object SyncRoot => Gate;

    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            if (_leaseCount == 0)
            {
                fpdfview.FPDF_InitLibrary();
            }

            checked
            {
                _leaseCount++;
            }

            return new Lease();
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            if (_leaseCount <= 0)
            {
                return;
            }

            _leaseCount--;
            if (_leaseCount == 0)
            {
                fpdfview.FPDF_DestroyLibrary();
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        ~Lease()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Release();
            GC.SuppressFinalize(this);
        }
    }
}
