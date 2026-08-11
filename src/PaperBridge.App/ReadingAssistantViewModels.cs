using PaperBridge.Application.Reading;

namespace PaperBridge.App;

public sealed class ReadingEvidenceViewModel
{
    public ReadingEvidenceViewModel(EvidenceCandidate evidence)
    {
        Evidence = evidence;
    }

    public EvidenceCandidate Evidence { get; }

    public string CitationId => Evidence.CitationId;

    public string PageLabel => $"第 {Evidence.PageIndex + 1} 页";

    public string SectionTitle => Evidence.SectionTitle;

    public string EnglishExcerpt => Evidence.EnglishExcerpt;
}

public sealed class ReadingMessageViewModel
{
    public ReadingMessageViewModel(bool isUser, string text)
    {
        IsUser = isUser;
        Text = text;
    }

    public bool IsUser { get; }

    public string Speaker => IsUser ? "我" : "AI";

    public string Text { get; }
}
