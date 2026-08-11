using System.Collections.ObjectModel;
using System.Windows;
using PaperBridge.Domain.Documents;

namespace PaperBridge.App;

public partial class DocumentOrganizationDialog : Window
{
    public DocumentOrganizationDialog(
        LibraryDocument document,
        IReadOnlyList<LibraryFolder> folders)
    {
        InitializeComponent();
        DocumentTitle = document.Title;
        FolderChoices.Add(new FolderChoice(null, "未分类"));
        foreach (var folder in folders)
        {
            FolderChoices.Add(new FolderChoice(folder.Id, folder.Name));
        }

        SelectedFolderChoice = FolderChoices.First(choice => choice.Id == document.FolderId);
        TagsText = string.Join(", ", document.Tags ?? Array.Empty<string>());
        IsFavorite = document.IsFavorite;
        DataContext = this;
    }

    public string DocumentTitle { get; }

    public ObservableCollection<FolderChoice> FolderChoices { get; } = [];

    public FolderChoice SelectedFolderChoice { get; set; }

    public string TagsText { get; set; }

    public bool IsFavorite { get; set; }

    public Guid? FolderId => SelectedFolderChoice.Id;

    public IReadOnlyList<string> Tags { get; private set; } = [];

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var tags = TagsText
            .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tags.Length > 32 || tags.Any(tag => tag.Length > 40 || tag.Any(char.IsControl)))
        {
            MessageBox.Show(
                this,
                "每篇文献最多使用 32 个标签，每个标签最多 40 个字符。",
                "标签无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Tags = tags;
        DialogResult = true;
    }

    public sealed record FolderChoice(Guid? Id, string Name);
}
