using PaperBridge.Application.Glossaries;
using PaperBridge.Domain.Glossaries;

namespace PaperBridge.Domain.Tests.Glossaries;

public sealed class GlossaryDelimitedTextTests
{
    [Theory]
    [InlineData(',')]
    [InlineData('\t')]
    public void WriteAndParse_RoundTripsQuotedFieldsAndAliases(char delimiter)
    {
        var term = new GlossaryTerm(
            "neutron flux",
            "中子通量",
            GlossarySource.User,
            priority: 7,
            category: "中子学",
            explanation: "line one,\nline two",
            sourceReference: "source \"A\"",
            englishAliases: ["flux", "neutron-flux"],
            chineseAliases: ["通量"],
            notes: "checked",
            reviewStatus: GlossaryReviewStatus.Approved);

        var text = GlossaryDelimitedText.Write([term], delimiter);
        var row = Assert.Single(GlossaryDelimitedText.Parse(text, delimiter));

        Assert.Equal(term.English, row.English);
        Assert.Equal(term.PreferredChinese, row.PreferredChinese);
        Assert.Equal(term.EnglishAliases, row.EnglishAliases);
        Assert.Equal(term.Explanation, row.Explanation);
        Assert.Equal(term.SourceReference, row.SourceReference);
        Assert.Equal(7, row.Priority);
        Assert.Equal(GlossaryReviewStatus.Approved, row.ReviewStatus);
    }

    [Fact]
    public void Write_EscapesSpreadsheetFormulaPrefixes()
    {
        var term = new GlossaryTerm("term", "=cmd", GlossarySource.User);

        var text = GlossaryDelimitedText.Write([term], ',');

        Assert.Contains("'=cmd", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingRequiredHeader()
    {
        Assert.Throws<FormatException>(() => GlossaryDelimitedText.Parse("english,category\nterm,x", ','));
    }
}
