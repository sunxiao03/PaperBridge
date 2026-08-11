using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PaperBridge.Application.Abstractions;

namespace PaperBridge.App;

public sealed class PdfPageViewModel : INotifyPropertyChanged
{
    private ImageSource? _image;
    private string _status = "等待渲染";
    private bool _isLoading;
    private bool _isTextLoading;
    private PdfPageText? _pageText;

    public PdfPageViewModel(Guid tabId, int pageIndex)
    {
        TabId = tabId;
        PageIndex = pageIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid TabId { get; }

    public int PageIndex { get; }

    public ObservableCollection<AnnotationOverlayItem> AnnotationOverlays { get; } = [];

    public string Header => $"第 {PageIndex + 1} 页";

    public ImageSource? Image
    {
        get => _image;
        private set => SetField(ref _image, value);
    }

    public PdfPageText? PageText
    {
        get => _pageText;
        private set => SetField(ref _pageText, value);
    }

    public bool TryBeginTextLoading()
    {
        if (_isTextLoading || PageText is not null)
        {
            return false;
        }

        _isTextLoading = true;
        return true;
    }

    public void SetPageText(PdfPageText pageText)
    {
        PageText = pageText;
        _isTextLoading = false;
    }

    public void CancelTextLoading() => _isTextLoading = false;

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool TryBeginLoading()
    {
        if (_isLoading || Image is not null)
        {
            return false;
        }

        _isLoading = true;
        Status = "正在渲染...";
        return true;
    }

    public void SetImage(ImageSource image)
    {
        Image = image;
        Status = string.Empty;
        _isLoading = false;
    }

    public void SetFailure(string message)
    {
        Status = message;
        _isLoading = false;
    }

    public void CancelLoading()
    {
        if (_isLoading)
        {
            Status = "等待渲染";
            _isLoading = false;
        }
    }

    public void ReleaseImage()
    {
        Image = null;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
