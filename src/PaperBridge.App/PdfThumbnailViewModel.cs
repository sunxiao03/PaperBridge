using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace PaperBridge.App;

public sealed class PdfThumbnailViewModel : INotifyPropertyChanged
{
    private ImageSource? _image;
    private string _status = "等待";
    private bool _isLoading;

    public PdfThumbnailViewModel(Guid tabId, int pageIndex)
    {
        TabId = tabId;
        PageIndex = pageIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid TabId { get; }

    public int PageIndex { get; }

    public string PageLabel => $"{PageIndex + 1}";

    public ImageSource? Image
    {
        get => _image;
        private set => SetField(ref _image, value);
    }

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
        Status = "渲染中...";
        return true;
    }

    public void SetImage(ImageSource image)
    {
        Image = image;
        Status = string.Empty;
        _isLoading = false;
    }

    public void SetFailure()
    {
        Status = "失败";
        _isLoading = false;
    }

    public void CancelLoading()
    {
        if (_isLoading)
        {
            Status = "等待";
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
