namespace PaperBridge.Infrastructure.Pdf;

public sealed class PdfiumException : Exception
{
    public PdfiumException(string message, PdfiumError error)
        : base(message)
    {
        Error = error;
    }

    public PdfiumError Error { get; }
}

