using System.Collections.ObjectModel;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Bilingual;
using PaperBridge.Application.Reading;
using PaperBridge.Domain.Documents;
using PaperBridge.Domain.Translations;
using PaperBridge.Infrastructure.Pdf;

namespace PaperBridge.App;

public sealed class ReaderTabSession : IDisposable
{
    public ReaderTabSession(LibraryDocument libraryDocument, PdfiumDocument document)
    {
        LibraryDocument = libraryDocument;
        Document = document;
        LastKnownPageIndex = Math.Clamp(
            libraryDocument.LastPageIndex,
            0,
            Math.Max(0, document.PageCount - 1));
        LastKnownScrollOffset = Math.Max(0, libraryDocument.LastScrollOffset);

        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            Pages.Add(new PdfPageViewModel(Id, pageIndex));
            Thumbnails.Add(new PdfThumbnailViewModel(Id, pageIndex));
        }
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Title => LibraryDocument.Title;

    public LibraryDocument LibraryDocument { get; set; }

    public ObservableCollection<PdfPageViewModel> Pages { get; } = [];

    public ObservableCollection<PdfThumbnailViewModel> Thumbnails { get; } = [];

    public ObservableCollection<PdfOutlineItem> OutlineItems { get; } = [];

    public ObservableCollection<BilingualSegmentViewModel> BilingualSegments { get; } = [];

    public ObservableCollection<AnnotationViewModel> Annotations { get; } = [];

    public ObservableCollection<ReadingMessageViewModel> ReadingMessages { get; } = [];

    public ObservableCollection<ReadingEvidenceViewModel> ReadingEvidence { get; } = [];

    internal PdfiumDocument Document { get; }

    internal CancellationTokenSource Lifetime { get; } = new();

    internal CancellationTokenSource TranslationActivity { get; private set; } = new();

    internal CancellationTokenSource? TranslationRequest { get; private set; }

    internal CancellationTokenSource BilingualActivity { get; private set; } = new();

    internal CancellationTokenSource? FullTranslationRequest { get; private set; }

    internal CancellationTokenSource? ReadingRequest { get; private set; }

    internal bool IsFullTranslationActive => FullTranslationRequest is { IsCancellationRequested: false };

    internal Dictionary<int, CancellationTokenSource> PageLoads { get; } = [];

    internal Dictionary<int, CancellationTokenSource> ThumbnailLoads { get; } = [];

    internal int LastKnownPageIndex { get; set; }

    internal double LastKnownScrollOffset { get; set; }

    internal bool IsClosing { get; private set; }

    internal int TranslationPageIndex { get; set; } = -1;

    internal string TranslationSourceText { get; set; } = string.Empty;

    internal string TranslationOutputText { get; set; } = string.Empty;

    internal string TranslationMachineOutputText { get; set; } = string.Empty;

    internal string TranslationStatus { get; set; } = "请在 PDF 原文上拖选要翻译的文字";

    internal TranslationGranularity LastTranslationGranularity { get; set; } = TranslationGranularity.Selection;

    internal string LastTranslationInput { get; set; } = string.Empty;

    internal BilingualDisplayMode BilingualDisplayMode { get; set; } = BilingualDisplayMode.Pdf;

    internal string BilingualStatus { get; set; } = "尚未生成双语内容";

    internal string ReadingAssistantOutput { get; set; } = string.Empty;

    internal string ReadingAssistantStatus { get; set; } = "可解释选区、总结或就当前文献提问";

    internal string ReadingQuestion { get; set; } = string.Empty;

    internal DocumentCorpus? ReadingCorpus { get; set; }

    internal void Deactivate()
    {
        CancelTransientWork();
        ResetTranslationActivity();
        foreach (var page in Pages)
        {
            page.CancelLoading();
            page.ReleaseImage();
        }

        foreach (var thumbnail in Thumbnails)
        {
            thumbnail.CancelLoading();
            thumbnail.ReleaseImage();
        }

        ReadingCorpus = null;
    }

    public void Dispose()
    {
        if (IsClosing)
        {
            return;
        }

        IsClosing = true;
        Lifetime.Cancel();
        BilingualActivity.Cancel();
        FullTranslationRequest?.Cancel();
        TranslationActivity.Cancel();
        TranslationRequest?.Cancel();
        ReadingRequest?.Cancel();
        Deactivate();
        Document.Dispose();
        TranslationRequest?.Dispose();
        TranslationActivity.Dispose();
        FullTranslationRequest?.Dispose();
        ReadingRequest?.Dispose();
        BilingualActivity.Dispose();
        Lifetime.Dispose();
    }

    internal CancellationTokenSource BeginTranslationRequest()
    {
        TranslationRequest?.Cancel();
        TranslationRequest?.Dispose();
        TranslationRequest = CancellationTokenSource.CreateLinkedTokenSource(TranslationActivity.Token);
        return TranslationRequest;
    }

    internal void CompleteTranslationRequest(CancellationTokenSource request)
    {
        if (ReferenceEquals(TranslationRequest, request))
        {
            TranslationRequest = null;
        }

        request.Dispose();
    }

    internal void CancelTranslationRequest() => TranslationRequest?.Cancel();

    internal void ResetTranslationRequests() => ResetTranslationActivity();

    internal void ResetBilingualRequests()
    {
        FullTranslationRequest?.Cancel();
        FullTranslationRequest = null;
        BilingualActivity.Cancel();
        BilingualActivity.Dispose();
        BilingualActivity = new CancellationTokenSource();
    }

    internal CancellationTokenSource BeginFullTranslation()
    {
        FullTranslationRequest?.Cancel();
        FullTranslationRequest = CancellationTokenSource.CreateLinkedTokenSource(BilingualActivity.Token);
        return FullTranslationRequest;
    }

    internal void CancelFullTranslation() => FullTranslationRequest?.Cancel();

    internal void CompleteFullTranslation(CancellationTokenSource request)
    {
        if (ReferenceEquals(FullTranslationRequest, request))
        {
            FullTranslationRequest = null;
        }

        request.Dispose();
    }

    internal bool IsCurrentTranslationRequest(CancellationTokenSource request) =>
        ReferenceEquals(TranslationRequest, request);

    internal CancellationTokenSource BeginReadingRequest()
    {
        ReadingRequest?.Cancel();
        ReadingRequest?.Dispose();
        ReadingRequest = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        return ReadingRequest;
    }

    internal void CompleteReadingRequest(CancellationTokenSource request)
    {
        if (ReferenceEquals(ReadingRequest, request))
        {
            ReadingRequest = null;
        }

        request.Dispose();
    }

    internal void CancelReadingRequest() => ReadingRequest?.Cancel();

    internal bool IsCurrentReadingRequest(CancellationTokenSource request) =>
        ReferenceEquals(ReadingRequest, request);

    internal void ResetReadingRequests()
    {
        ReadingRequest?.Cancel();
        ReadingRequest?.Dispose();
        ReadingRequest = null;
        ReadingCorpus = null;
    }

    private void ResetTranslationActivity()
    {
        TranslationRequest?.Cancel();
        TranslationRequest?.Dispose();
        TranslationRequest = null;
        TranslationActivity.Cancel();
        TranslationActivity.Dispose();
        TranslationActivity = new CancellationTokenSource();
    }

    private void CancelTransientWork()
    {
        foreach (var cancellation in PageLoads.Values)
        {
            cancellation.Cancel();
        }

        PageLoads.Clear();
        foreach (var cancellation in ThumbnailLoads.Values)
        {
            cancellation.Cancel();
        }

        ThumbnailLoads.Clear();
    }
}
