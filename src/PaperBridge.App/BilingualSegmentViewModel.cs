using System.ComponentModel;
using System.Runtime.CompilerServices;
using PaperBridge.Application.Bilingual;

namespace PaperBridge.App;

public sealed class BilingualSegmentViewModel : INotifyPropertyChanged
{
    private string _editableTranslation;
    private bool _hasUserTranslation;

    public BilingualSegmentViewModel(StoredBilingualSegment segment)
    {
        Segment = segment;
        _editableTranslation = segment.DisplayTranslation;
        _hasUserTranslation = segment.HasUserTranslation;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StoredBilingualSegment Segment { get; private set; }

    public int PageIndex => Segment.PageIndex;

    public string PageLabel => $"第 {PageIndex + 1} 页 · {Segment.SegmentId}";

    public string SourceText => Segment.SourceText;

    public string MachineTranslation => Segment.MachineTranslation;

    public string EditableTranslation
    {
        get => _editableTranslation;
        set
        {
            if (_editableTranslation == value)
            {
                return;
            }

            _editableTranslation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
        }
    }

    public bool HasUserTranslation
    {
        get => _hasUserTranslation;
        private set
        {
            _hasUserTranslation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TranslationKind));
        }
    }

    public bool IsModified => !string.Equals(EditableTranslation, Segment.DisplayTranslation, StringComparison.Ordinal);

    public string TranslationKind => HasUserTranslation ? "用户编辑稿（重译不会覆盖）" : "机器译文";

    public void MarkUserSaved()
    {
        HasUserTranslation = true;
        Segment = Segment with
        {
            UserTranslation = EditableTranslation.Trim(),
            UserUpdatedAtUtc = DateTimeOffset.UtcNow
        };
        OnPropertyChanged(nameof(IsModified));
    }

    public void RestoreMachineTranslation()
    {
        Segment = Segment with { UserTranslation = null, UserUpdatedAtUtc = null };
        _editableTranslation = Segment.MachineTranslation;
        HasUserTranslation = false;
        OnPropertyChanged(nameof(EditableTranslation));
        OnPropertyChanged(nameof(IsModified));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum BilingualDisplayMode
{
    Pdf,
    Paragraph,
    Comparison
}
