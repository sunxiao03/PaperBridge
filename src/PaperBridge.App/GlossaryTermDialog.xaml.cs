using System.Windows;
using PaperBridge.Domain.Glossaries;

namespace PaperBridge.App;

public partial class GlossaryTermDialog : Window
{
    private readonly GlossaryDefinition _glossary;
    private readonly GlossaryTerm? _original;

    public GlossaryTermDialog(
        GlossaryDefinition glossary,
        GlossaryTerm? term = null,
        string? initialEnglish = null,
        string? initialChinese = null)
    {
        _glossary = glossary;
        _original = term;
        InitializeComponent();
        ReviewStatusBox.ItemsSource = Enum.GetValues<GlossaryReviewStatus>();
        EnglishBox.Text = term?.English ?? initialEnglish ?? string.Empty;
        EnglishBox.IsReadOnly = term is not null;
        ChineseBox.Text = term?.PreferredChinese ?? initialChinese ?? string.Empty;
        EnglishAliasesBox.Text = string.Join("; ", term?.EnglishAliases ?? []);
        ChineseAliasesBox.Text = string.Join("; ", term?.ChineseAliases ?? []);
        CategoryBox.Text = term?.Category ?? glossary.Topic ?? string.Empty;
        ExplanationBox.Text = term?.Explanation ?? string.Empty;
        NotesBox.Text = term?.Notes ?? string.Empty;
        SourceReferenceBox.Text = term?.SourceReference ?? string.Empty;
        PriorityBox.Text = (term?.Priority ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ReviewStatusBox.SelectedItem = term?.ReviewStatus ??
            (glossary.Source == GlossarySource.User ? GlossaryReviewStatus.Approved : GlossaryReviewStatus.Pending);
        Loaded += (_, _) => EnglishBox.Focus();
    }

    public GlossaryTerm? ResultTerm { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var english = EnglishBox.Text.Trim();
            var chinese = ChineseBox.Text.Trim();
            if (english.Length is < 1 or > 256 || chinese.Length is < 1 or > 256)
            {
                throw new ArgumentException("英文术语和首选中文必须为 1–256 个字符。");
            }

            if (!int.TryParse(PriorityBox.Text, out var priority) || priority is < -10000 or > 10000)
            {
                throw new ArgumentException("优先级必须是 -10000 到 10000 之间的整数。");
            }

            ResultTerm = new GlossaryTerm(
                english,
                chinese,
                _glossary.Source,
                priority,
                CategoryBox.Text,
                ExplanationBox.Text,
                SourceReferenceBox.Text,
                _original?.Id,
                _glossary.Id,
                SplitAliases(EnglishAliasesBox.Text),
                SplitAliases(ChineseAliasesBox.Text),
                NotesBox.Text,
                ReviewStatusBox.SelectedItem is GlossaryReviewStatus status
                    ? status
                    : GlossaryReviewStatus.Pending,
                DateTimeOffset.UtcNow);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存术语", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static IReadOnlyList<string> SplitAliases(string value) => value
        .Split([';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
