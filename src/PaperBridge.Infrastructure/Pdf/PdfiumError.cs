namespace PaperBridge.Infrastructure.Pdf;

public enum PdfiumError : ulong
{
    Success = 0,
    Unknown = 1,
    File = 2,
    Format = 3,
    Password = 4,
    Security = 5,
    Page = 6
}

