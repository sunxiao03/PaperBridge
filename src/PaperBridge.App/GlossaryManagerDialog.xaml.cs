using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Glossaries;
using PaperBridge.Domain.Glossaries;

namespace PaperBridge.App;

public partial class GlossaryManagerDialog : Window
{
    private readonly IGlossaryStore _store;
    private GlossarySnapshot? _snapshot;
    private bool _refreshing;

    public GlossaryManagerDialog(IGlossaryStore store)
    {
        _store = store;
        InitializeComponent();
        Loaded += GlossaryManagerDialog_Loaded;
    }

    private GlossaryDefinition? SelectedGlossary => GlossaryList.SelectedItem as GlossaryDefinition;

    private GlossaryTerm? SelectedTerm => TermsGrid.SelectedItem as GlossaryTerm;

    private async void GlossaryManagerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= GlossaryManagerDialog_Loaded;
        await ReloadAsync();
    }

    private async Task ReloadAsync(Guid? selectGlossaryId = null)
    {
        try
        {
            _refreshing = true;
            var previousId = selectGlossaryId ?? SelectedGlossary?.Id;
            _snapshot = await _store.GetSnapshotAsync();
            GlossaryList.ItemsSource = _snapshot.Glossaries;
            GlossaryList.SelectedItem = _snapshot.Glossaries.FirstOrDefault(item => item.Id == previousId) ??
                _snapshot.Glossaries.FirstOrDefault();
            RefreshTerms();
        }
        catch (Exception exception)
        {
            ShowError("无法加载术语库", exception);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void GlossaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing)
        {
            RefreshTerms();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshTerms();

    private void RefreshTerms()
    {
        if (_snapshot is null || SelectedGlossary is not { } glossary)
        {
            TermsGrid.ItemsSource = null;
            return;
        }

        _refreshing = true;
        try
        {
            EnabledCheckBox.IsChecked = glossary.IsEnabled;
            EnabledCheckBox.IsEnabled = glossary.Source != GlossarySource.User ||
                _snapshot.Glossaries.Count(item => item.Source == GlossarySource.User && item.IsEnabled) > 1;
            SelectedGlossaryTitle.Text = $"{glossary.Name} · {glossary.Source}";
            var query = SearchBox.Text.Trim();
            var terms = _snapshot.Terms.Where(term => term.GlossaryId == glossary.Id);
            if (query.Length > 0)
            {
                terms = terms.Where(term =>
                    term.English.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    term.PreferredChinese.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    term.EnglishAliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    term.ChineseAliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (term.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (term.Explanation?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (term.Notes?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (term.SourceReference?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var visible = terms.OrderBy(term => term.English, StringComparer.Ordinal).ToArray();
            TermsGrid.ItemsSource = visible;
            var all = _snapshot.Terms.Where(term => term.GlossaryId == glossary.Id).ToArray();
            SummaryText.Text = $"共 {all.Length} 条 · 已审核 {all.Count(term => term.ReviewStatus == GlossaryReviewStatus.Approved)} · 待审核 {all.Count(term => term.ReviewStatus == GlossaryReviewStatus.Pending)}";
            UpdateTermButtons();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void EnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || SelectedGlossary is not { } glossary)
        {
            return;
        }

        try
        {
            await _store.SetGlossaryEnabledAsync(glossary.Id, EnabledCheckBox.IsChecked == true);
            await ReloadAsync(glossary.Id);
        }
        catch (Exception exception)
        {
            ShowError("无法修改术语库状态", exception);
        }
    }

    private async void CreateGlossaryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("新建个人词库", "词库名称", "我的术语库") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        try
        {
            var glossary = await _store.CreatePersonalGlossaryAsync(dialog.Value);
            await ReloadAsync(glossary.Id);
        }
        catch (Exception exception)
        {
            ShowError("无法新建词库", exception);
        }
    }

    private async void AddTermButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGlossary is not { } glossary)
        {
            return;
        }

        var dialog = new GlossaryTermDialog(glossary) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultTerm is { } term)
        {
            await SaveAndReloadAsync(term, glossary.Id);
        }
    }

    private async void EditTermButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGlossary is not { } glossary || SelectedTerm is not { } term)
        {
            return;
        }

        var dialog = new GlossaryTermDialog(glossary, term) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultTerm is { } result)
        {
            await SaveAndReloadAsync(result, glossary.Id);
        }
    }

    private async void DeleteTermButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGlossary is not { Source: GlossarySource.User } glossary || SelectedTerm is not { } term ||
            MessageBox.Show(this, $"删除个人术语“{term.English}”？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _store.DeleteTermAsync(term.Id);
            await ReloadAsync(glossary.Id);
        }
        catch (Exception exception)
        {
            ShowError("无法删除术语", exception);
        }
    }

    private async void ApproveButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeReviewStatusAsync(GlossaryReviewStatus.Approved);

    private async void RejectButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeReviewStatusAsync(GlossaryReviewStatus.Rejected);

    private async Task ChangeReviewStatusAsync(GlossaryReviewStatus status)
    {
        if (SelectedTerm is not { } term || SelectedGlossary is not { } glossary)
        {
            return;
        }

        var updated = CopyTerm(term, status);
        await SaveAndReloadAsync(updated, glossary.Id);
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGlossary is not { Source: GlossarySource.User } glossary)
        {
            MessageBox.Show(this, "导入仅写入个人词库；请选择个人词库。", "无法导入", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new OpenFileDialog { Filter = "术语表 (*.csv;*.tsv)|*.csv;*.tsv|CSV (*.csv)|*.csv|TSV (*.tsv)|*.tsv" };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(picker.FileName);
            var rows = GlossaryDelimitedText.Parse(content, Path.GetExtension(picker.FileName).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',');
            foreach (var row in rows)
            {
                await _store.SaveTermAsync(new GlossaryTerm(
                    row.English, row.PreferredChinese, glossary.Source, row.Priority, row.Category,
                    row.Explanation, row.SourceReference, glossaryId: glossary.Id,
                    englishAliases: row.EnglishAliases, chineseAliases: row.ChineseAliases,
                    notes: row.Notes, reviewStatus: row.ReviewStatus));
            }

            await ReloadAsync(glossary.Id);
            MessageBox.Show(this, $"已导入 {rows.Count} 条；重复英文按当前文件更新。", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            ShowError("无法导入术语", exception);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || SelectedGlossary is not { } glossary)
        {
            return;
        }

        var picker = new SaveFileDialog
        {
            FileName = glossary.Name + ".csv",
            DefaultExt = ".csv",
            Filter = "CSV UTF-8 (*.csv)|*.csv|TSV UTF-8 (*.tsv)|*.tsv"
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var delimiter = Path.GetExtension(picker.FileName).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
            var terms = _snapshot.Terms.Where(term => term.GlossaryId == glossary.Id);
            await File.WriteAllTextAsync(picker.FileName, GlossaryDelimitedText.Write(terms, delimiter), new System.Text.UTF8Encoding(true));
            MessageBox.Show(this, "术语表已导出。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError("无法导出术语", exception);
        }
    }

    private void TermsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTermButtons();

    private void UpdateTermButtons()
    {
        var hasTerm = SelectedTerm is not null;
        EditTermButton.IsEnabled = hasTerm;
        DeleteTermButton.IsEnabled = hasTerm && SelectedGlossary?.Source == GlossarySource.User;
        ApproveButton.IsEnabled = hasTerm && SelectedTerm?.ReviewStatus != GlossaryReviewStatus.Approved;
        RejectButton.IsEnabled = hasTerm && SelectedTerm?.ReviewStatus != GlossaryReviewStatus.Rejected;
    }

    private async Task SaveAndReloadAsync(GlossaryTerm term, Guid glossaryId)
    {
        try
        {
            await _store.SaveTermAsync(term);
            await ReloadAsync(glossaryId);
        }
        catch (Exception exception)
        {
            ShowError("无法保存术语", exception);
        }
    }

    private static GlossaryTerm CopyTerm(GlossaryTerm term, GlossaryReviewStatus status) => new(
        term.English, term.PreferredChinese, term.Source, term.Priority, term.Category, term.Explanation,
        term.SourceReference, term.Id, term.GlossaryId, term.EnglishAliases, term.ChineseAliases,
        term.Notes, status, DateTimeOffset.UtcNow);

    private void ShowError(string title, Exception exception) =>
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
