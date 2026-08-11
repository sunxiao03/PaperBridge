using System.Windows;
using PaperBridge.Application.Annotations;

namespace PaperBridge.App;

public partial class AnnotationEditorDialog : Window
{
    public AnnotationEditorDialog(
        AnnotationKind kind,
        string? selectedText,
        bool hasTranslation,
        string? initialNote = null,
        string? initialColor = null,
        bool initiallyLinked = false)
    {
        InitializeComponent();
        DescriptionText.Text = kind == AnnotationKind.Bookmark
            ? "保存当前页面书签"
            : $"保存{KindText(kind)}：{TrimPreview(selectedText)}";
        NoteTextBox.Text = initialNote ?? string.Empty;
        ColorCombo.ItemsSource = new[]
        {
            new ColorOption("黄色", "#FFE066"),
            new ColorOption("蓝色", "#4D96FF"),
            new ColorOption("橙色", "#FF922B"),
            new ColorOption("红色", "#FF6B6B"),
            new ColorOption("绿色", "#51CF66")
        };
        ColorCombo.SelectedValue = initialColor ?? DefaultColor(kind);
        LinkTranslationCheckBox.IsEnabled = hasTranslation;
        LinkTranslationCheckBox.IsChecked = hasTranslation && initiallyLinked;
        Loaded += (_, _) => NoteTextBox.Focus();
    }

    public string? ResultNote { get; private set; }

    public string ResultColor { get; private set; } = "#FFE066";

    public bool LinkTranslation { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (NoteTextBox.Text.Length > 10_000)
        {
            MessageBox.Show(this, "批注不能超过 10,000 个字符。", "批注过长", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultNote = string.IsNullOrWhiteSpace(NoteTextBox.Text) ? null : NoteTextBox.Text.Trim();
        ResultColor = ColorCombo.SelectedValue as string ?? "#FFE066";
        LinkTranslation = LinkTranslationCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private static string KindText(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => "高亮",
        AnnotationKind.Underline => "下划线",
        AnnotationKind.Note => "批注",
        _ => kind.ToString()
    };

    private static string DefaultColor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Underline => "#4D96FF",
        AnnotationKind.Note => "#FF922B",
        AnnotationKind.Bookmark => "#FF6B6B",
        _ => "#FFE066"
    };

    private static string TrimPreview(string? value) => value?.Length > 80 ? value[..80] + "…" : value ?? string.Empty;

    private sealed record ColorOption(string Label, string Value);
}
