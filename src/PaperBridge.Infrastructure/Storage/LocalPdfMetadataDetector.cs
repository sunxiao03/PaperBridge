using System.Text.RegularExpressions;
using PaperBridge.Application.Abstractions;

namespace PaperBridge.Infrastructure.Storage;

internal static partial class LocalPdfMetadataDetector
{
    public static DetectedPdfMetadata Detect(
        PdfDocumentMetadata embedded,
        string firstPageText,
        string fallbackTitle)
    {
        ArgumentNullException.ThrowIfNull(embedded);
        ArgumentNullException.ThrowIfNull(firstPageText);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackTitle);

        var title = Normalize(embedded.Title);
        if (title is null || string.Equals(title, "untitled", StringComparison.OrdinalIgnoreCase))
        {
            title = fallbackTitle.Trim();
        }

        var doiSearchText = string.Join(
            Environment.NewLine,
            firstPageText,
            embedded.Subject,
            embedded.Keywords);
        var doi = NormalizeDoi(DoiPattern().Match(doiSearchText).Value);

        return new DetectedPdfMetadata(
            title,
            Normalize(embedded.Author),
            ExtractYear(embedded.CreationDate),
            Journal: null,
            doi);
    }

    private static int? ExtractYear(string? pdfDate)
    {
        if (string.IsNullOrWhiteSpace(pdfDate))
        {
            return null;
        }

        var match = YearPattern().Match(pdfDate);
        return match.Success && int.TryParse(match.Groups["year"].Value, out var year)
            ? year
            : null;
    }

    private static string? NormalizeDoi(string? value)
    {
        value = Normalize(value)?.TrimEnd('.', ',', ';', ':');
        if (value is null)
        {
            return null;
        }

        while (value.EndsWith(')') && value.Count(character => character == ')') > value.Count(character => character == '('))
        {
            value = value[..^1];
        }

        return value.ToLowerInvariant();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"(?<![A-Za-z0-9])10\.\d{4,9}/[-._;()/:A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex DoiPattern();

    [GeneratedRegex(@"(?:D:)?(?<year>(?:19|20)\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();
}

internal sealed record DetectedPdfMetadata(
    string Title,
    string? Authors,
    int? PublicationYear,
    string? Journal,
    string? Doi);

