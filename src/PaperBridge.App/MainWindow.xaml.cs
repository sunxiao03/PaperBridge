using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Annotations;
using PaperBridge.Application.Bilingual;
using PaperBridge.Application.Caching;
using PaperBridge.Application.Glossaries;
using PaperBridge.Application.Reading;
using PaperBridge.Application.Translation;
using PaperBridge.Domain.Documents;
using PaperBridge.Domain.Glossaries;
using PaperBridge.Domain.Translations;
using PaperBridge.Infrastructure.Pdf;
using PaperBridge.Infrastructure.Security;
using PaperBridge.Infrastructure.Storage;
using PaperBridge.Infrastructure.Translation;

namespace PaperBridge.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const double PreviewScale = 2.25;
    private const double ThumbnailScale = 0.26;
    private readonly AppDataPaths _paths;
    private readonly ManagedDocumentLibrary _library;
    private readonly SqliteGlossaryStore _glossaryStore;
    private readonly SqliteBilingualSegmentStore _bilingualStore;
    private readonly SqliteAnnotationStore _annotationStore;
    private readonly BoundedPriorityWorkQueue _bilingualWorkQueue = new(maximumQueued: 32, maximumConcurrency: 2);
    private readonly JsonTranslationSettingsStore _translationSettingsStore;
    private readonly WindowsCredentialStore _credentialStore;
    private readonly ByteBudgetLruCache<PageCacheKey, PdfRenderedPage> _pageCache =
        new(256L * 1024 * 1024, page => page.ByteSize);
    private readonly ByteBudgetLruCache<PageCacheKey, PdfRenderedPage> _thumbnailCache =
        new(32L * 1024 * 1024, page => page.ByteSize);
    private readonly HashSet<DocumentId> _documentsOpening = [];
    private readonly DispatcherTimer _readingPositionTimer;
    private ReaderTabSession? _activeTab;
    private int _searchVersion;
    private int _tabSwitchVersion;
    private bool _changingTabSelection;
    private bool _libraryInitialized;
    private bool _refreshingFolderFilters;
    private bool _refreshingTagFilters;
    private bool _restoringReadingPosition;
    private TranslationServiceSettings _translationSettings = TranslationServiceSettings.Default;
    private TranslationCoordinator? _translationCoordinator;
    private ReadingAssistantCoordinator? _readingAssistantCoordinator;
    private HttpClient? _translationHttpClient;
    private string? _translationUnavailableMessage = "尚未配置 API Key；请打开设置。";
    private int _bilingualRefreshVersion;
    private bool _syncingComparisonScroll;
    private bool _librarySidebarCollapsed;
    private bool _readerNavigationCollapsed;
    private Window? _readingAssistantWindow;

    public MainWindow()
    {
        _paths = AppDataPaths.CreateDefault();
        _library = new ManagedDocumentLibrary(_paths);
        _glossaryStore = new SqliteGlossaryStore(_paths);
        _bilingualStore = new SqliteBilingualSegmentStore(_paths);
        _annotationStore = new SqliteAnnotationStore(_paths);
        _translationSettingsStore = new JsonTranslationSettingsStore(_paths);
        _credentialStore = new WindowsCredentialStore();
        InitializeComponent();
        DataContext = this;
        ImportPdfButton.IsEnabled = false;
        RenameFolderButton.IsEnabled = false;
        DeleteFolderButton.IsEnabled = false;
        Loaded += MainWindow_Loaded;

        _readingPositionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _readingPositionTimer.Tick += ReadingPositionTimer_Tick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LibraryDocument> LibraryDocuments { get; } = [];

    public ObservableCollection<LibraryFolder> LibraryFolders { get; } = [];

    public ObservableCollection<LibraryFolderFilterOption> FolderFilters { get; } = [];

    public ObservableCollection<LibraryTagFilterOption> TagFilters { get; } = [];

    public ObservableCollection<ReaderTabSession> OpenTabs { get; } = [];

    public ReaderTabSession? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (ReferenceEquals(_activeTab, value))
            {
                return;
            }

            _activeTab = value;
            OnPropertyChanged();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _readingPositionTimer.Stop();
        _readingAssistantWindow?.Close();
        if (_libraryInitialized && ActiveTab is { } activeTab)
        {
            try
            {
                CaptureReadingPosition(activeTab);
                PersistReadingPositionAsync(activeTab).GetAwaiter().GetResult();
            }
            catch
            {
                // Application shutdown must continue if a final local database write fails.
            }
        }

        foreach (var tab in OpenTabs.ToArray())
        {
            RemoveCachedPages(tab);
            tab.Dispose();
        }

        OpenTabs.Clear();
        _pageCache.Clear();
        _thumbnailCache.Clear();
        _translationCoordinator?.Dispose();
        _readingAssistantCoordinator?.Dispose();
        _translationHttpClient?.Dispose();
        _bilingualWorkQueue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnClosed(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        StatusText.Text = "正在初始化本地文献库...";

        try
        {
            await _library.InitializeAsync();
            await _glossaryStore.InitializeAsync();
            _libraryInitialized = true;
            ImportPdfButton.IsEnabled = true;
            await ReloadFoldersAsync();
            await ReloadTagFiltersAsync();
            await ReloadLibraryAsync(SearchBox.Text);
            await InitializeTranslationRuntimeAsync();
            StatusText.Text = $"文献库已就绪 · {LibraryDocuments.Count} 篇";
        }
        catch (Exception exception)
        {
            StatusText.Text = "文献库初始化失败";
            MessageBox.Show(this, exception.Message, "无法初始化文献库", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportPdfButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入英文文献",
            Filter = "PDF 文档 (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImportPdfButton.IsEnabled = false;
        var importedCount = 0;
        var duplicateCount = 0;
        var failures = new List<string>();
        LibraryDocument? lastDocument = null;

        try
        {
            for (var index = 0; index < dialog.FileNames.Length; index++)
            {
                var filePath = dialog.FileNames[index];
                StatusText.Text = $"正在导入 {index + 1}/{dialog.FileNames.Length}：{System.IO.Path.GetFileName(filePath)}";

                try
                {
                    var result = await _library.ImportPdfAsync(filePath);
                    lastDocument = result.Document;
                    importedCount += result.WasImported ? 1 : 0;
                    duplicateCount += result.WasDuplicate ? 1 : 0;
                }
                catch (Exception exception)
                {
                    failures.Add($"{System.IO.Path.GetFileName(filePath)}：{exception.Message}");
                }
            }

            await ReloadLibraryAsync(SearchBox.Text);
            StatusText.Text = $"导入完成 · 新增 {importedCount} · 重复 {duplicateCount} · 失败 {failures.Count}";

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, failures),
                    "部分文献导入失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (dialog.FileNames.Length == 1 && lastDocument is not null)
            {
                await OpenLibraryDocumentAsync(lastDocument);
            }
        }
        finally
        {
            ImportPdfButton.IsEnabled = _libraryInitialized;
        }
    }

    private async void LibraryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LibraryList.SelectedItem is LibraryDocument document)
        {
            await OpenLibraryDocumentAsync(document);
        }
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_libraryInitialized)
        {
            await ReloadLibraryAsync(SearchBox.Text);
        }
    }

    private async void FolderFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFolderManagementButtons();
        if (_libraryInitialized && !_refreshingFolderFilters)
        {
            await ReloadLibraryAsync(SearchBox.Text);
        }
    }

    private async void LibraryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_libraryInitialized && !_refreshingTagFilters)
        {
            await ReloadLibraryAsync(SearchBox.Text);
        }
    }

    private async void CreateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("新建分类", "分类名称：") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var folder = await _library.CreateFolderAsync(dialog.Value);
            await ReloadFoldersAsync(folder.Id);
            await ReloadLibraryAsync(SearchBox.Text);
            StatusText.Text = $"已创建分类：{folder.Name}";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ShowLibraryOperationError("无法创建分类", exception);
        }
    }

    private async void RenameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedFolder() is not { } folder)
        {
            return;
        }

        var dialog = new TextInputDialog("重命名分类", "新的分类名称：", folder.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _library.RenameFolderAsync(folder.Id, dialog.Value);
            await ReloadFoldersAsync(folder.Id);
            await RefreshOpenTabsAsync();
            await ReloadLibraryAsync(SearchBox.Text);
            StatusText.Text = $"分类已重命名为：{dialog.Value}";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ShowLibraryOperationError("无法重命名分类", exception);
        }
    }

    private async void DeleteFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedFolder() is not { } folder ||
            MessageBox.Show(
                this,
                $"删除逻辑分类“{folder.Name}”？其中的文献将变为未分类，PDF 文件不会被删除。",
                "删除分类",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _library.DeleteFolderAsync(folder.Id);
            await ReloadFoldersAsync();
            await RefreshOpenTabsAsync();
            await ReloadLibraryAsync(SearchBox.Text);
            StatusText.Text = $"已删除分类：{folder.Name}；文献已保留";
        }
        catch (KeyNotFoundException exception)
        {
            ShowLibraryOperationError("无法删除分类", exception);
        }
    }

    private async void EditDocumentOrganization_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDocument(sender) is not { } document)
        {
            return;
        }

        var dialog = new DocumentOrganizationDialog(document, LibraryFolders) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _library.SetDocumentTagsAsync(document.Id, dialog.Tags);
            await _library.SetDocumentFolderAsync(document.Id, dialog.FolderId);
            await _library.SetFavoriteAsync(document.Id, dialog.IsFavorite);
            await RefreshOpenTabsAsync();
            await ReloadTagFiltersAsync();
            await ReloadLibraryAsync(SearchBox.Text);
            StatusText.Text = $"已更新归类：{document.Title}";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ShowLibraryOperationError("无法更新文献归类", exception);
        }
    }

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDocument(sender) is not { } document)
        {
            return;
        }

        try
        {
            await _library.SetFavoriteAsync(document.Id, !document.IsFavorite);
            await RefreshOpenTabsAsync();
            await ReloadLibraryAsync(SearchBox.Text);
            StatusText.Text = document.IsFavorite ? "已取消收藏" : "已收藏";
        }
        catch (KeyNotFoundException exception)
        {
            ShowLibraryOperationError("无法更新收藏状态", exception);
        }
    }

    private async void RemoveDocumentKeepFile_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDocument(sender) is { } document)
        {
            await RemoveDocumentAsync(document, deleteManagedFile: false);
        }
    }

    private async void DeleteManagedDocument_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextDocument(sender) is not { } document ||
            MessageBox.Show(
                this,
                $"永久删除“{document.Title}”的受管理 PDF 副本？\n\n导入时的原始 PDF 不受影响，此操作无法在 PaperBridge 中撤销。",
                "永久删除受管理副本",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RemoveDocumentAsync(document, deleteManagedFile: true);
    }

    private async Task RemoveDocumentAsync(LibraryDocument document, bool deleteManagedFile)
    {
        try
        {
            var openTab = OpenTabs.FirstOrDefault(tab => tab.LibraryDocument.Id == document.Id);
            if (openTab is not null)
            {
                await CloseTabAsync(openTab);
            }

            await _library.RemoveDocumentAsync(document.Id, deleteManagedFile);
            await ReloadTagFiltersAsync();
            await ReloadLibraryAsync(SearchBox.Text);
            StatusText.Text = deleteManagedFile
                ? "受管理 PDF 副本已永久删除；原始文件未修改"
                : "文献已从库中移除；受管理 PDF 文件已保留";
        }
        catch (Exception exception) when (
            exception is System.IO.IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            ShowLibraryOperationError("无法移除文献", exception);
        }
    }

    private async Task ReloadFoldersAsync(Guid? selectedFolderId = null)
    {
        var folders = await _library.GetFoldersAsync();
        _refreshingFolderFilters = true;
        try
        {
            LibraryFolders.Clear();
            FolderFilters.Clear();
            FolderFilters.Add(new LibraryFolderFilterOption(LibraryFolderFilterKind.All, "全部文献"));
            FolderFilters.Add(new LibraryFolderFilterOption(LibraryFolderFilterKind.Unfiled, "未分类"));
            foreach (var folder in folders)
            {
                LibraryFolders.Add(folder);
                FolderFilters.Add(new LibraryFolderFilterOption(
                    LibraryFolderFilterKind.Folder,
                    folder.Name,
                    folder.Id));
            }

            FolderFilterCombo.SelectedItem = selectedFolderId is null
                ? FolderFilters[0]
                : FolderFilters.FirstOrDefault(option => option.FolderId == selectedFolderId) ?? FolderFilters[0];
        }
        finally
        {
            _refreshingFolderFilters = false;
        }

        UpdateFolderManagementButtons();
    }

    private async Task ReloadTagFiltersAsync()
    {
        var selectedTag = (TagFilterCombo.SelectedItem as LibraryTagFilterOption)?.Tag;
        var documents = await _library.GetDocumentsAsync();
        var tags = documents
            .SelectMany(document => document.Tags ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _refreshingTagFilters = true;
        try
        {
            TagFilters.Clear();
            TagFilters.Add(new LibraryTagFilterOption("全部标签"));
            foreach (var tag in tags)
            {
                TagFilters.Add(new LibraryTagFilterOption(tag, tag));
            }

            TagFilterCombo.SelectedItem = selectedTag is null
                ? TagFilters[0]
                : TagFilters.FirstOrDefault(option =>
                    string.Equals(option.Tag, selectedTag, StringComparison.OrdinalIgnoreCase)) ?? TagFilters[0];
        }
        finally
        {
            _refreshingTagFilters = false;
        }
    }

    private async Task ReloadLibraryAsync(string query)
    {
        var searchVersion = ++_searchVersion;
        var documents = await _library.SearchAsync(query);
        if (searchVersion != _searchVersion)
        {
            return;
        }

        IEnumerable<LibraryDocument> filtered = documents;
        if (FolderFilterCombo.SelectedItem is LibraryFolderFilterOption folderFilter)
        {
            filtered = folderFilter.Kind switch
            {
                LibraryFolderFilterKind.Unfiled => filtered.Where(document => document.FolderId is null),
                LibraryFolderFilterKind.Folder => filtered.Where(document => document.FolderId == folderFilter.FolderId),
                _ => filtered
            };
        }

        if (FavoritesOnlyCheckBox.IsChecked == true)
        {
            filtered = filtered.Where(document => document.IsFavorite);
        }

        if (TagFilterCombo.SelectedItem is LibraryTagFilterOption { Tag: { } selectedTag })
        {
            filtered = filtered.Where(document =>
                document.Tags?.Contains(selectedTag, StringComparer.OrdinalIgnoreCase) == true);
        }

        LibraryDocuments.Clear();
        foreach (var document in filtered)
        {
            LibraryDocuments.Add(document);
        }
    }

    private async Task RefreshOpenTabsAsync()
    {
        if (OpenTabs.Count == 0)
        {
            return;
        }

        var documents = await _library.GetDocumentsAsync();
        var byId = documents.ToDictionary(document => document.Id);
        foreach (var tab in OpenTabs)
        {
            if (byId.TryGetValue(tab.LibraryDocument.Id, out var document))
            {
                tab.LibraryDocument = document;
            }
        }
    }

    private async Task InitializeTranslationRuntimeAsync()
    {
        _translationCoordinator?.Dispose();
        _translationCoordinator = null;
        _readingAssistantCoordinator?.Dispose();
        _readingAssistantCoordinator = null;
        _translationHttpClient?.Dispose();
        _translationHttpClient = null;

        try
        {
            _translationSettings = await _translationSettingsStore.LoadAsync();
            var account = TranslationCredentialAccounts.ForProvider(_translationSettings.ProviderId);
            var apiKey = await _credentialStore.GetAsync(account);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _translationUnavailableMessage = "尚未配置 API Key；请打开设置。";
                TranslationStateText.Text = _translationUnavailableMessage;
                ReadingStatusText.Text = _translationUnavailableMessage;
                return;
            }

            _translationHttpClient = new HttpClient();
            var provider = new OpenAiCompatibleTranslationProvider(
                _translationHttpClient,
                _translationSettings.ProviderId,
                _translationSettings.BaseUrl,
                apiKey);
            _translationCoordinator = new TranslationCoordinator(
                provider,
                new SqliteTranslationCache(_paths),
                new TranslationExecutionOptions(
                    _translationSettings.MaxConcurrency,
                    TimeSpan.FromSeconds(_translationSettings.RequestTimeoutSeconds)));
            var readingProvider = new OpenAiCompatibleReadingAssistantProvider(
                _translationHttpClient,
                _translationSettings.ProviderId,
                _translationSettings.BaseUrl,
                apiKey);
            _readingAssistantCoordinator = new ReadingAssistantCoordinator(
                readingProvider,
                new SqliteReadingAssistantCache(_paths),
                maximumConcurrency: Math.Min(2, _translationSettings.MaxConcurrency),
                requestTimeout: TimeSpan.FromSeconds(_translationSettings.RequestTimeoutSeconds));
            _translationUnavailableMessage = null;
            TranslationStateText.Text = $"已配置 {_translationSettings.ProviderId} · {_translationSettings.Model}";
            ReadingStatusText.Text = $"已配置 {_translationSettings.ProviderId} · {_translationSettings.Model}";
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or ArgumentException or System.ComponentModel.Win32Exception or
                System.IO.IOException or UnauthorizedAccessException)
        {
            _translationSettings = TranslationServiceSettings.Default;
            _translationCoordinator = null;
            _readingAssistantCoordinator?.Dispose();
            _readingAssistantCoordinator = null;
            _translationHttpClient?.Dispose();
            _translationHttpClient = null;
            _translationUnavailableMessage = $"翻译设置无法加载：{exception.Message}";
            TranslationStateText.Text = _translationUnavailableMessage;
            ReadingStatusText.Text = _translationUnavailableMessage;
        }
    }

    private async void TranslationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentKey = await _credentialStore.GetAsync(
                TranslationCredentialAccounts.ForProvider(_translationSettings.ProviderId));
            var dialog = new TranslationSettingsDialog(_translationSettings, !string.IsNullOrWhiteSpace(currentKey))
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.ResultSettings is not { } settings)
            {
                return;
            }

            if (dialog.DeleteKeyForProviderId is { } deleteProviderId)
            {
                await _credentialStore.DeleteAsync(TranslationCredentialAccounts.ForProvider(deleteProviderId));
            }

            if (dialog.NewApiKey is { } newApiKey)
            {
                await _credentialStore.SaveAsync(
                    TranslationCredentialAccounts.ForProvider(settings.ProviderId),
                    newApiKey);
            }

            await _translationSettingsStore.SaveAsync(settings);
            foreach (var tab in OpenTabs)
            {
                tab.ResetTranslationRequests();
                tab.ResetBilingualRequests();
                tab.ResetReadingRequests();
            }

            await InitializeTranslationRuntimeAsync();
            if (ActiveTab is { } activeTab)
            {
                activeTab.TranslationStatus = _translationCoordinator is null
                    ? "尚未配置 API Key；请打开设置。"
                    : $"已配置 {settings.ProviderId} · {settings.Model}";
                RestoreTranslationPanel(activeTab);
                RestoreReadingAssistantPanel(activeTab);
            }

            StatusText.Text = "翻译设置已保存";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or
                System.IO.IOException or UnauthorizedAccessException)
        {
            ShowLibraryOperationError("无法保存翻译设置", exception);
        }
    }

    private void GlossaryManagerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GlossaryManagerDialog(_glossaryStore) { Owner = this };
        dialog.ShowDialog();
        StatusText.Text = "术语库已更新；后续翻译将使用最新的已审核词条";
    }

    private async void CreateHighlightButton_Click(object sender, RoutedEventArgs e) =>
        await CreateTextAnnotationAsync(AnnotationKind.Highlight);

    private async void CreateUnderlineButton_Click(object sender, RoutedEventArgs e) =>
        await CreateTextAnnotationAsync(AnnotationKind.Underline);

    private async void CreateNoteButton_Click(object sender, RoutedEventArgs e) =>
        await CreateTextAnnotationAsync(AnnotationKind.Note);

    private async Task CreateTextAnnotationAsync(AnnotationKind kind)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        var pageIndex = tab.LastKnownPageIndex;
        if (tab.TranslationPageIndex != pageIndex)
        {
            MessageBox.Show(
                this,
                "请先在当前 PDF 页面上拖选要标记的原文。",
                "当前页没有原文选区",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var selectionStart = TranslationSourceTextBox.SelectionStart;
        var selectionLength = TranslationSourceTextBox.SelectionLength;
        var selectedText = TranslationSourceTextBox.SelectedText.Trim();
        if (selectionLength <= 0 || selectedText.Length == 0)
        {
            MessageBox.Show(this, "请先在 PDF 原文上拖选文字。", "没有原文选区", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var translation = GetCurrentTranslationSnapshot(tab, pageIndex);
        var dialog = new AnnotationEditorDialog(kind, selectedText, translation is not null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var page = await tab.Document.ExtractPageTextAsync(pageIndex, tab.Lifetime.Token);
            if (!string.Equals(page.Text, TranslationSourceTextBox.Text, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("PDF 文字层已经变化，无法可靠映射当前选区；请重新在原文上拖选。");
            }

            var annotation = AnnotationAnchorService.CreateTextAnnotation(
                tab.LibraryDocument.Id,
                tab.LibraryDocument.ContentHash,
                page,
                selectionStart,
                selectionLength,
                kind,
                dialog.ResultColor,
                dialog.ResultNote,
                dialog.LinkTranslation ? translation : null);
            await _annotationStore.SaveAsync(annotation);
            await ReloadAnnotationsAsync(tab);
            StatusText.Text = $"已保存{GetAnnotationKindText(kind)} · 第 {pageIndex + 1} 页";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            Microsoft.Data.Sqlite.SqliteException or IOException or PdfiumException)
        {
            ShowLibraryOperationError("无法保存原文标记", exception);
        }
    }

    private async void ToggleBookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        var pageIndex = tab.LastKnownPageIndex;
        var existing = tab.Annotations.FirstOrDefault(item =>
            item.Annotation.Kind == AnnotationKind.Bookmark && item.Annotation.PageIndex == pageIndex);
        try
        {
            if (existing is not null)
            {
                await _annotationStore.DeleteAsync(existing.Annotation.Id);
                await ReloadAnnotationsAsync(tab);
                StatusText.Text = $"已移除第 {pageIndex + 1} 页书签";
                return;
            }

            var translation = GetCurrentTranslationSnapshot(tab, pageIndex);
            var dialog = new AnnotationEditorDialog(AnnotationKind.Bookmark, null, translation is not null)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var bookmark = AnnotationAnchorService.CreateBookmark(
                tab.LibraryDocument.Id,
                tab.LibraryDocument.ContentHash,
                pageIndex,
                dialog.ResultColor,
                dialog.ResultNote,
                dialog.LinkTranslation ? translation : null);
            await _annotationStore.SaveAsync(bookmark);
            await ReloadAnnotationsAsync(tab);
            StatusText.Text = $"已添加第 {pageIndex + 1} 页书签";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            ShowLibraryOperationError("无法更新页面书签", exception);
        }
    }

    private void AnnotationFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            ApplyAnnotationFilter(tab);
        }
    }

    private void AnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var enabled = AnnotationList.SelectedItem is AnnotationViewModel;
        NavigateAnnotationButton.IsEnabled = enabled;
        EditAnnotationButton.IsEnabled = enabled;
        DeleteAnnotationButton.IsEnabled = enabled;
    }

    private async void NavigateAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (AnnotationList.SelectedItem is not AnnotationViewModel item || ActiveTab is not { } tab)
        {
            return;
        }

        tab.LastKnownPageIndex = Math.Clamp(item.Annotation.PageIndex, 0, tab.Document.PageCount - 1);
        if (tab.BilingualDisplayMode == BilingualDisplayMode.Pdf)
        {
            NavigateToPage(tab.LastKnownPageIndex);
        }
        else
        {
            await RefreshBilingualViewAsync(tab, tab.LastKnownPageIndex);
        }

        StatusText.Text = item.Annotation.AnchorStatus is AnnotationAnchorStatus.Valid or AnnotationAnchorStatus.Migrated
            ? $"已定位{item.KindText} · 第 {item.Annotation.PageIndex + 1} 页"
            : $"已定位原页，但锚点{item.StatusText}：{item.ResolutionMessage}";
    }

    private async void EditAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (AnnotationList.SelectedItem is not AnnotationViewModel item || ActiveTab is not { } tab)
        {
            return;
        }

        var annotation = item.Annotation;
        var currentTranslation = GetCurrentTranslationSnapshot(tab, annotation.PageIndex) ?? annotation.LinkedTranslation;
        var dialog = new AnnotationEditorDialog(
            annotation.Kind,
            annotation.SelectedText,
            currentTranslation is not null,
            annotation.NoteText,
            annotation.Color,
            annotation.LinkedTranslation is not null)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var updated = annotation with
            {
                NoteText = dialog.ResultNote,
                Color = dialog.ResultColor,
                LinkedTranslation = dialog.LinkTranslation ? currentTranslation : null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _annotationStore.SaveAsync(updated);
            await ReloadAnnotationsAsync(tab);
            StatusText.Text = "批注已更新";
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            ShowLibraryOperationError("无法更新批注", exception);
        }
    }

    private async void DeleteAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (AnnotationList.SelectedItem is not AnnotationViewModel item || ActiveTab is not { } tab ||
            MessageBox.Show(this, $"删除该{item.KindText}？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _annotationStore.DeleteAsync(item.Annotation.Id);
            await ReloadAnnotationsAsync(tab);
            StatusText.Text = "标记已删除";
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            ShowLibraryOperationError("无法删除标记", exception);
        }
    }

    private async Task ReloadAnnotationsAsync(ReaderTabSession tab)
    {
        var stored = await _annotationStore.GetForDocumentAsync(tab.LibraryDocument.Id, tab.Lifetime.Token);
        var resolvedItems = new List<AnnotationViewModel>(stored.Count);
        var loadedPageIndex = -1;
        PdfPageText? loadedPage = null;
        foreach (var annotation in stored.OrderBy(annotation => annotation.PageIndex).ThenBy(annotation => annotation.SourceStart))
        {
            PdfPageText? page = null;
            if (annotation.IsTextAnchor &&
                string.Equals(annotation.DocumentHash, tab.LibraryDocument.ContentHash, StringComparison.OrdinalIgnoreCase) &&
                annotation.PageIndex >= 0 && annotation.PageIndex < tab.Document.PageCount)
            {
                if (loadedPageIndex != annotation.PageIndex)
                {
                    loadedPage = await tab.Document.ExtractPageTextAsync(annotation.PageIndex, tab.Lifetime.Token);
                    loadedPageIndex = annotation.PageIndex;
                }

                page = loadedPage;
            }

            var resolution = AnnotationAnchorService.Resolve(annotation, tab.LibraryDocument.ContentHash, page);
            if (resolution.WasChanged)
            {
                await _annotationStore.SaveAsync(resolution.Annotation, tab.Lifetime.Token);
            }

            resolvedItems.Add(new AnnotationViewModel(resolution.Annotation, resolution.Message));
        }

        foreach (var page in tab.Pages)
        {
            page.AnnotationOverlays.Clear();
        }

        tab.Annotations.Clear();
        foreach (var item in resolvedItems.OrderBy(item => item.Annotation.PageIndex).ThenBy(item => item.Annotation.SourceStart))
        {
            tab.Annotations.Add(item);
            var annotation = item.Annotation;
            if (annotation.PageIndex < 0 || annotation.PageIndex >= tab.Pages.Count)
            {
                continue;
            }

            var rectangles = annotation.Kind == AnnotationKind.Bookmark
                ? new[] { new NormalizedPdfRectangle(0.94, 0.015, 0.045, 0.065) }
                : annotation.Rectangles;
            tab.Pages[annotation.PageIndex].AnnotationOverlays.Add(new AnnotationOverlayItem(
                annotation.Id,
                annotation.Kind == AnnotationKind.Bookmark ? AnnotationKind.Note : annotation.Kind,
                annotation.Color,
                rectangles,
                annotation.AnchorStatus));
        }

        if (ReferenceEquals(ActiveTab, tab))
        {
            ApplyAnnotationFilter(tab);
        }
    }

    private void ApplyAnnotationFilter(ReaderTabSession tab)
    {
        var tag = (AnnotationFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        IEnumerable<AnnotationViewModel> items = tab.Annotations;
        if (string.Equals(tag, "Invalid", StringComparison.Ordinal))
        {
            items = items.Where(item => item.Annotation.AnchorStatus is
                AnnotationAnchorStatus.Orphaned or AnnotationAnchorStatus.DocumentChanged);
        }
        else if (Enum.TryParse<AnnotationKind>(tag, out var kind))
        {
            items = items.Where(item => item.Annotation.Kind == kind);
        }

        AnnotationList.ItemsSource = items.ToArray();
        AnnotationList.SelectedItem = null;
    }

    private string? GetCurrentTranslationSnapshot(ReaderTabSession tab, int pageIndex)
    {
        if (tab.TranslationPageIndex == pageIndex && !string.IsNullOrWhiteSpace(TranslationOutputTextBox.Text))
        {
            return TranslationOutputTextBox.Text.Trim();
        }

        return tab.BilingualSegments.FirstOrDefault(segment => segment.PageIndex == pageIndex)?.EditableTranslation;
    }

    private static string GetAnnotationKindText(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => "高亮",
        AnnotationKind.Underline => "下划线",
        AnnotationKind.Note => "批注",
        AnnotationKind.Bookmark => "书签",
        _ => kind.ToString()
    };

    private void ShowPdfViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            SetBilingualDisplayMode(tab, BilingualDisplayMode.Pdf);
            NavigateToPage(tab.LastKnownPageIndex);
        }
    }

    private async void ShowParagraphViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        SetBilingualDisplayMode(tab, BilingualDisplayMode.Paragraph);
        await RefreshBilingualViewAsync(tab, tab.LastKnownPageIndex);
        QueueCurrentPageAndPrefetch(tab);
    }

    private async void ShowComparisonViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        SetBilingualDisplayMode(tab, BilingualDisplayMode.Comparison);
        await RefreshBilingualViewAsync(tab, tab.LastKnownPageIndex);
        QueueCurrentPageAndPrefetch(tab);
    }

    private void TranslateBilingualPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            QueueBilingualPageTranslation(tab, tab.LastKnownPageIndex, 0, tab.BilingualActivity.Token);
            UpdateQueueStatus();
        }
    }

    private async void TranslateFullDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        if (_translationCoordinator is null)
        {
            SetBilingualStatus(tab, "尚未配置可用的 API Key；无法启动全文翻译。");
            return;
        }

        var request = tab.BeginFullTranslation();
        CancelFullTranslationButton.IsEnabled = true;
        var pageOrder = Enumerable.Range(tab.LastKnownPageIndex, tab.Document.PageCount - tab.LastKnownPageIndex)
            .Concat(Enumerable.Range(0, tab.LastKnownPageIndex))
            .ToArray();
        var submitted = 0;
        try
        {
            foreach (var pageIndex in pageOrder)
            {
                request.Token.ThrowIfCancellationRequested();
                while (true)
                {
                    var result = QueueBilingualPageTranslation(
                        tab,
                        pageIndex,
                        100 + Math.Abs(pageIndex - tab.LastKnownPageIndex),
                        request.Token);
                    if (result is WorkQueueEnqueueResult.Accepted or WorkQueueEnqueueResult.Duplicate)
                    {
                        submitted++;
                        break;
                    }

                    await Task.Delay(100, request.Token);
                }

                SetBilingualStatus(tab, $"全文翻译已提交 {submitted}/{tab.Document.PageCount} 页；队列上限 32。", false);
                UpdateQueueStatus();
            }

            while ((_bilingualWorkQueue.PendingCount > 0 || _bilingualWorkQueue.ActiveCount > 0) &&
                   !request.IsCancellationRequested)
            {
                await Task.Delay(250, request.Token);
                UpdateQueueStatus();
            }

            SetBilingualStatus(tab, $"全文翻译批次已完成 · {submitted} 页。", false);
        }
        catch (OperationCanceledException)
        {
            SetBilingualStatus(tab, "全文翻译已取消；已完成的段落仍保存在本地。", false);
        }
        finally
        {
            tab.CompleteFullTranslation(request);
            if (ReferenceEquals(ActiveTab, tab))
            {
                CancelFullTranslationButton.IsEnabled = false;
            }

            UpdateQueueStatus();
        }
    }

    private void CancelFullTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            tab.CancelFullTranslation();
            SetBilingualStatus(tab, "正在取消全文翻译...", false);
        }
    }

    private async void PreviousBilingualPageButton_Click(object sender, RoutedEventArgs e) =>
        await MoveBilingualPageAsync(-1);

    private async void NextBilingualPageButton_Click(object sender, RoutedEventArgs e) =>
        await MoveBilingualPageAsync(1);

    private async Task MoveBilingualPageAsync(int delta)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        var pageIndex = Math.Clamp(tab.LastKnownPageIndex + delta, 0, tab.Document.PageCount - 1);
        tab.LastKnownPageIndex = pageIndex;
        await RefreshBilingualViewAsync(tab, pageIndex);
        QueueBilingualPageTranslation(tab, pageIndex, 0, tab.BilingualActivity.Token);
        QueueBilingualPageTranslation(tab, Math.Min(pageIndex + 1, tab.Document.PageCount - 1), 10, tab.BilingualActivity.Token);
        UpdateQueueStatus();
    }

    private async void SaveBilingualEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BilingualSegmentViewModel segment } || ActiveTab is not { } tab)
        {
            return;
        }

        try
        {
            await _bilingualStore.SaveUserTranslationAsync(
                tab.LibraryDocument.ContentHash,
                segment.PageIndex,
                segment.Segment.SegmentId,
                segment.EditableTranslation);
            segment.MarkUserSaved();
            SetBilingualStatus(tab, "用户编辑稿已保存；后续机器重译不会覆盖它。", false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            ShowLibraryOperationError("无法保存双语编辑稿", exception);
        }
    }

    private async void RestoreMachineTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BilingualSegmentViewModel segment } || ActiveTab is not { } tab)
        {
            return;
        }

        if (segment.HasUserTranslation && MessageBox.Show(
                this,
                "删除该段的用户编辑稿并恢复机器译文？",
                "恢复机器译文",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _bilingualStore.SaveUserTranslationAsync(
                tab.LibraryDocument.ContentHash,
                segment.PageIndex,
                segment.Segment.SegmentId,
                null);
            segment.RestoreMachineTranslation();
            SetBilingualStatus(tab, "已恢复机器译文。", false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            ShowLibraryOperationError("无法恢复机器译文", exception);
        }
    }

    private void CompareBilingualTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BilingualSegmentViewModel segment })
        {
            var dialog = new TranslationComparisonDialog(
                segment.MachineTranslation,
                segment.EditableTranslation)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
    }

    private void BilingualList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: BilingualSegmentViewModel segment } && ActiveTab is { } tab)
        {
            tab.LastKnownPageIndex = segment.PageIndex;
            StatusText.Text = $"双语段落映射到第 {segment.PageIndex + 1} 页";
            if (!ReferenceEquals(ComparisonOriginalList.SelectedItem, segment))
            {
                ComparisonOriginalList.SelectedItem = segment;
            }

            if (!ReferenceEquals(ComparisonTranslationList.SelectedItem, segment))
            {
                ComparisonTranslationList.SelectedItem = segment;
            }
        }
    }

    private void ComparisonOriginalList_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        SynchronizeComparisonScroll(ComparisonOriginalList, ComparisonTranslationList, e);

    private void ComparisonTranslationList_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        SynchronizeComparisonScroll(ComparisonTranslationList, ComparisonOriginalList, e);

    private void SynchronizeComparisonScroll(ListBox source, ListBox target, ScrollChangedEventArgs e)
    {
        if (_syncingComparisonScroll || e.VerticalChange == 0)
        {
            return;
        }

        var sourceScroll = FindVisualChild<ScrollViewer>(source);
        var targetScroll = FindVisualChild<ScrollViewer>(target);
        if (sourceScroll is null || targetScroll is null)
        {
            return;
        }

        _syncingComparisonScroll = true;
        try
        {
            var ratio = sourceScroll.ScrollableHeight <= 0 ? 0 : sourceScroll.VerticalOffset / sourceScroll.ScrollableHeight;
            targetScroll.ScrollToVerticalOffset(ratio * targetScroll.ScrollableHeight);
        }
        finally
        {
            _syncingComparisonScroll = false;
        }
    }

    private void QueueCurrentPageAndPrefetch(ReaderTabSession tab)
    {
        if (_translationCoordinator is null)
        {
            SetBilingualStatus(tab, "尚未配置可用的 API Key；已显示本地已有译文。", false);
            return;
        }

        for (var offset = 0; offset < 3 && tab.LastKnownPageIndex + offset < tab.Document.PageCount; offset++)
        {
            QueueBilingualPageTranslation(
                tab,
                tab.LastKnownPageIndex + offset,
                offset == 0 ? 0 : 10 + offset,
                tab.BilingualActivity.Token);
        }

        UpdateQueueStatus();
    }

    private WorkQueueEnqueueResult QueueBilingualPageTranslation(
        ReaderTabSession tab,
        int pageIndex,
        int priority,
        CancellationToken ownerToken)
    {
        if (_translationCoordinator is not { } coordinator || tab.IsClosing ||
            pageIndex < 0 || pageIndex >= tab.Document.PageCount)
        {
            return WorkQueueEnqueueResult.Duplicate;
        }

        var key = $"{tab.Id:N}:{pageIndex}";
        var settings = _translationSettings;
        return _bilingualWorkQueue.TryEnqueue(
            key,
            priority,
            token => TranslateBilingualPageAsync(tab, pageIndex, coordinator, settings, token),
            ownerToken);
    }

    private async Task TranslateBilingualPageAsync(
        ReaderTabSession tab,
        int pageIndex,
        TranslationCoordinator coordinator,
        TranslationServiceSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReportBilingualStatusAsync(tab, $"正在分析并翻译第 {pageIndex + 1} 页...", cancellationToken);
            var page = await tab.Document.ExtractPageTextAsync(pageIndex, cancellationToken);
            var analysis = PageLayoutAnalyzer.Analyze(page);
            if (analysis.Paragraphs.Count == 0)
            {
                await ReportBilingualStatusAsync(tab, $"第 {pageIndex + 1} 页没有可翻译文本。", cancellationToken);
                return;
            }

            var snapshot = await _glossaryStore.GetSnapshotAsync(cancellationToken);
            var existing = (await _bilingualStore.GetPageAsync(
                tab.LibraryDocument.ContentHash,
                pageIndex,
                cancellationToken)).ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
            var context = page.Text.Length <= 4_000 ? page.Text : page.Text[..4_000];
            var completed = 0;
            foreach (var paragraph in analysis.Paragraphs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var constraints = GlossaryConstraintResolver.Resolve(snapshot, paragraph.Text, context);
                var granularity = analysis.RecommendedMode == BilingualLayoutMode.Paragraph
                    ? TranslationGranularity.Paragraph
                    : TranslationGranularity.Page;
                var job = new TranslationJob(
                    tab.LibraryDocument.ContentHash,
                    paragraph.Text,
                    settings.Model,
                    granularity,
                    context,
                    constraints.Terminology,
                    constraints.Version,
                    TranslationJob.VersionCustomInstruction(settings.CustomInstruction),
                    settings.CustomInstruction);
                var result = await coordinator.TranslateAsync(job, cancellationToken);
                existing.TryGetValue(paragraph.SegmentId, out var previous);
                var sourceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(paragraph.Text)));
                await _bilingualStore.UpsertMachineTranslationAsync(new StoredBilingualSegment(
                    tab.LibraryDocument.ContentHash,
                    pageIndex,
                    paragraph.SegmentId,
                    paragraph.SourceStart,
                    paragraph.SourceLength,
                    paragraph.Text,
                    sourceHash,
                    result.Translation,
                    previous?.UserTranslation,
                    settings.ProviderId,
                    result.Model,
                    TranslationCoordinator.PromptVersion,
                    constraints.Version,
                    analysis.RecommendedMode,
                    analysis.Confidence,
                    analysis.DegradationReason,
                    DateTimeOffset.UtcNow,
                    previous?.UserUpdatedAtUtc), cancellationToken);
                completed++;
                await ReportBilingualStatusAsync(
                    tab,
                    $"第 {pageIndex + 1} 页 {completed}/{analysis.Paragraphs.Count} 段 · 队列 {_bilingualWorkQueue.PendingCount}",
                    cancellationToken);
            }

            var finalStatus = analysis.RecommendedMode == BilingualLayoutMode.Paragraph
                ? $"第 {pageIndex + 1} 页段落对齐完成 · 置信度 {analysis.Confidence:P0}"
                : $"第 {pageIndex + 1} 页已自动降级为按页对照：{analysis.DegradationReason}";
            await ReportBilingualStatusAsync(tab, finalStatus, cancellationToken);
            if (ReferenceEquals(ActiveTab, tab) && tab.LastKnownPageIndex == pageIndex &&
                tab.BilingualDisplayMode != BilingualDisplayMode.Pdf)
            {
                await RefreshBilingualViewAsync(tab, pageIndex, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is TranslationProviderException or TimeoutException or HttpRequestException or
            Microsoft.Data.Sqlite.SqliteException or IOException or PdfiumException or InvalidOperationException or ObjectDisposedException)
        {
            await ReportBilingualStatusAsync(tab, $"第 {pageIndex + 1} 页翻译失败：{exception.Message}", CancellationToken.None);
        }
        finally
        {
            await Dispatcher.InvokeAsync(UpdateQueueStatus);
        }
    }

    private async Task RefreshBilingualViewAsync(
        ReaderTabSession tab,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _bilingualRefreshVersion);
        var segments = await _bilingualStore.GetPageAsync(
            tab.LibraryDocument.ContentHash,
            pageIndex,
            cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            if (version != Volatile.Read(ref _bilingualRefreshVersion) || !ReferenceEquals(ActiveTab, tab))
            {
                return;
            }

            tab.BilingualSegments.Clear();
            foreach (var segment in segments.Take(200))
            {
                tab.BilingualSegments.Add(new BilingualSegmentViewModel(segment));
            }

            if (segments.Count == 0)
            {
                SetBilingualStatus(tab, $"第 {pageIndex + 1} 页尚无双语内容；已按当前页优先排队。", false);
                return;
            }

            var first = segments[0];
            if (tab.BilingualDisplayMode == BilingualDisplayMode.Paragraph &&
                first.LayoutMode == BilingualLayoutMode.PageAligned)
            {
                tab.BilingualDisplayMode = BilingualDisplayMode.Comparison;
                ApplyBilingualDisplayMode(tab);
            }

            var status = first.LayoutMode == BilingualLayoutMode.Paragraph
                ? $"第 {pageIndex + 1} 页 · 段落映射 · 置信度 {first.LayoutConfidence:P0}"
                : $"第 {pageIndex + 1} 页 · 按页对照 · {first.DegradationReason ?? "复杂版面自动降级"}";
            SetBilingualStatus(tab, status, false);
        });
    }

    private void SetBilingualDisplayMode(ReaderTabSession tab, BilingualDisplayMode mode)
    {
        tab.BilingualDisplayMode = mode;
        ApplyBilingualDisplayMode(tab);
    }

    private void ApplyBilingualDisplayMode(ReaderTabSession tab)
    {
        var pdf = tab.BilingualDisplayMode == BilingualDisplayMode.Pdf;
        PagesList.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        BilingualViewHost.Visibility = pdf ? Visibility.Collapsed : Visibility.Visible;
        ParagraphBilingualList.Visibility = tab.BilingualDisplayMode == BilingualDisplayMode.Paragraph
            ? Visibility.Visible
            : Visibility.Collapsed;
        ComparisonGrid.Visibility = tab.BilingualDisplayMode == BilingualDisplayMode.Comparison
            ? Visibility.Visible
            : Visibility.Collapsed;
        BilingualLayoutStatusText.Text = tab.BilingualStatus;
    }

    private async Task ReportBilingualStatusAsync(
        ReaderTabSession tab,
        string status,
        CancellationToken cancellationToken)
    {
        tab.BilingualStatus = status;
        await Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(ActiveTab, tab))
            {
                BilingualLayoutStatusText.Text = status;
                StatusText.Text = status;
            }
        }, DispatcherPriority.Background, cancellationToken);
    }

    private void SetBilingualStatus(ReaderTabSession tab, string status, bool updateMainStatus = true)
    {
        tab.BilingualStatus = status;
        if (ReferenceEquals(ActiveTab, tab))
        {
            BilingualLayoutStatusText.Text = status;
            if (updateMainStatus)
            {
                StatusText.Text = status;
            }
        }
    }

    private void UpdateQueueStatus() =>
        QueueStatusText.Text = $"队列 {_bilingualWorkQueue.PendingCount} · 执行 {_bilingualWorkQueue.ActiveCount}";

    private void LibrarySidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _librarySidebarCollapsed = !_librarySidebarCollapsed;
        LibrarySidebarColumn.Width = _librarySidebarCollapsed ? new GridLength(0) : new GridLength(280);
        LibrarySidebar.Visibility = _librarySidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LibrarySidebarToggleButton.ToolTip = _librarySidebarCollapsed ? "展开文献栏" : "收起文献栏";
    }

    private void ReaderNavigationToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _readerNavigationCollapsed = !_readerNavigationCollapsed;
        ReaderNavigationColumn.Width = _readerNavigationCollapsed ? new GridLength(0) : new GridLength(215);
    }

    private void ShowReadingAssistantButton_Click(object sender, RoutedEventArgs e) =>
        ShowReadingAssistantWindow();

    private void ShowReadingAssistantWindow()
    {
        if (_readingAssistantWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        if (ActiveTab is { } activeTab)
        {
            UpdateReadingAssistantContext(activeTab);
        }

        ReadingAssistantHost.Content = null;
        var window = new Window
        {
            Title = ActiveTab is null ? "PaperBridge · AI 阅读辅助" : $"AI 阅读辅助 · {ActiveTab.Title}",
            Owner = this,
            DataContext = this,
            Content = ReadingAssistantPanel,
            Width = 620,
            Height = 760,
            MinWidth = 480,
            MinHeight = 560,
            Background = new SolidColorBrush(Color.FromRgb(250, 250, 248)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.Closed += (_, _) =>
        {
            window.Content = null;
            ReadingAssistantHost.Content = ReadingAssistantPanel;
            _readingAssistantWindow = null;
        };
        _readingAssistantWindow = window;
        window.Show();
    }

    private async void ExplainSelectionButton_Click(object sender, RoutedEventArgs e) =>
        await ExplainReadingSelectionAsync(includeTranslation: false);

    private async void TranslateExplainSelectionButton_Click(object sender, RoutedEventArgs e) =>
        await ExplainReadingSelectionAsync(includeTranslation: true);

    private async Task ExplainReadingSelectionAsync(bool includeTranslation)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        var selection = tab.LastTranslationInput.Trim();
        if (selection.Length == 0)
        {
            SetReadingStatus(tab, "请先在 PDF 原文上拖选要解释的英文。");
            return;
        }

        if (selection.Length > 12_000)
        {
            SetReadingStatus(tab, "解释选区不能超过 12,000 字符。");
            return;
        }

        var context = CreateBoundedContext(tab.TranslationSourceText, selection);
        var completed = await ExecuteReadingActionAsync(
            tab,
            includeTranslation ? "正在翻译并解释选区..." : "正在解释选区...",
            async (coordinator, token) =>
            {
                var prompt = ReadingAssistantPromptFactory.ExplainSelection(
                    selection,
                    context,
                    includeTranslation,
                    _translationSettings.CustomInstruction);
                var result = await coordinator.CompleteAsync(new ReadingAssistantJob(
                    tab.LibraryDocument.ContentHash,
                    includeTranslation ? ReadingTaskKind.TranslateAndExplain : ReadingTaskKind.ExplainSelection,
                    _translationSettings.Model,
                    prompt.System,
                    prompt.User,
                    TranslationJob.VersionCustomInstruction(_translationSettings.CustomInstruction),
                    MaximumOutputTokens: 2_500), token);
                return new ReadingActionResult(
                    result.Content,
                    result.IsCacheHit ? "解释已从版本化缓存载入。" : "选区解释已完成。");
            });
        if (completed && ReferenceEquals(ActiveTab, tab))
        {
            ShowReadingAssistantWindow();
        }
    }

    private async void SummarizeSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        await ExecuteReadingActionAsync(
            tab,
            "正在构建当前文献的本地文本语料...",
            async (coordinator, token) =>
            {
                var corpus = await GetReadingCorpusAsync(tab, token);
                var pageIndex = Math.Clamp(tab.LastKnownPageIndex, 0, corpus.PageCount - 1);
                var section = corpus.Sections.Last(item => item.StartPageIndex <= pageIndex);
                SetReadingStatus(tab, $"正在总结：{FormatSectionRange(section)}...");
                var chunks = corpus.Chunks.Where(chunk =>
                    chunk.PageIndex >= section.StartPageIndex && chunk.PageIndex < section.EndPageIndexExclusive).ToArray();
                if (chunks.Length == 0)
                {
                    throw new InvalidOperationException("当前章节没有可总结的英文文本层。");
                }

                var summary = await SummarizeChunksAsync(coordinator, tab, chunks, section.Title, true, token);
                return new ReadingActionResult(summary, $"已总结当前章节：{section.Title}");
            });
    }

    private async void SummarizeDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab || MessageBox.Show(
                this,
                "全文总结会按文档长度发起多次 AI 请求，可能产生较多费用。是否继续？",
                "确认全文总结",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteReadingActionAsync(
            tab,
            "正在构建当前文献的本地文本语料...",
            async (coordinator, token) =>
            {
                var corpus = await GetReadingCorpusAsync(tab, token);
                if (corpus.Chunks.Count == 0)
                {
                    throw new InvalidOperationException("文档没有可总结的英文文本层。");
                }

                var summary = await SummarizeChunksAsync(
                    coordinator,
                    tab,
                    corpus.Chunks,
                    $"整篇论文（{corpus.PageCount} 页）",
                    false,
                    token);
                return new ReadingActionResult(summary, $"全文分块—综合总结已完成 · {corpus.PageCount} 页。");
            });
    }

    private async Task<string> SummarizeChunksAsync(
        ReadingAssistantCoordinator coordinator,
        ReaderTabSession tab,
        IReadOnlyList<DocumentTextChunk> chunks,
        string scopeLabel,
        bool isSection,
        CancellationToken cancellationToken)
    {
        var bundles = ReadingTextBundler.Bundle(chunks);
        var partials = new List<string>(bundles.Count);
        var callCount = 0;
        for (var index = 0; index < bundles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetReadingStatus(tab, $"正在总结分块 {index + 1}/{bundles.Count}...");
            var prompt = ReadingAssistantPromptFactory.SummarizeChunk(
                bundles[index],
                scopeLabel,
                _translationSettings.CustomInstruction);
            var result = await coordinator.CompleteAsync(new ReadingAssistantJob(
                tab.LibraryDocument.ContentHash,
                isSection && bundles.Count == 1 ? ReadingTaskKind.SectionSummary : ReadingTaskKind.DocumentChunkSummary,
                _translationSettings.Model,
                prompt.System,
                prompt.User,
                TranslationJob.VersionCustomInstruction(_translationSettings.CustomInstruction),
                MaximumOutputTokens: 2_200), cancellationToken);
            partials.Add(result.Content);
            callCount++;
        }

        while (partials.Count > 1)
        {
            var synthesisInputs = ReadingTextBundler.BundleText(partials);
            var next = new List<string>(synthesisInputs.Count);
            for (var index = 0; index < synthesisInputs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++callCount > 128)
                {
                    throw new InvalidOperationException("总结请求超过 128 次的阶段安全上限。");
                }

                SetReadingStatus(tab, $"正在综合总结 {index + 1}/{synthesisInputs.Count}...");
                var prompt = ReadingAssistantPromptFactory.SynthesizeSummary(
                    synthesisInputs[index],
                    scopeLabel,
                    _translationSettings.CustomInstruction);
                var result = await coordinator.CompleteAsync(new ReadingAssistantJob(
                    tab.LibraryDocument.ContentHash,
                    ReadingTaskKind.DocumentSynthesis,
                    _translationSettings.Model,
                    prompt.System,
                    prompt.User,
                    TranslationJob.VersionCustomInstruction(_translationSettings.CustomInstruction),
                    MaximumOutputTokens: 3_500), cancellationToken);
                next.Add(result.Content);
            }

            partials = next;
        }

        return partials.Single();
    }

    private async void AskReadingQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        var question = ReadingQuestionTextBox.Text.Trim();
        if (question.Length is < 1 or > 4_000)
        {
            SetReadingStatus(tab, "问题必须包含 1–4,000 个字符。");
            return;
        }

        var priorConversation = tab.ReadingMessages
            .Select(message => new ReadingConversationMessage(message.IsUser, message.Text))
            .ToArray();
        AppendReadingMessage(tab, new ReadingMessageViewModel(true, question));
        ReadingQuestionTextBox.Clear();
        await ExecuteReadingActionAsync(
            tab,
            "正在构建当前文献的本地文本语料...",
            async (coordinator, token) =>
            {
                var corpus = await GetReadingCorpusAsync(tab, token);
                SetReadingStatus(tab, "正在将问题展开为英文检索词...");
                var retrievalQuestion = string.Join("\n", priorConversation.TakeLast(4)
                    .Select(message => (message.IsUser ? "User: " : "Assistant: ") + message.Text)) + "\nQuestion: " + question;
                var expansionPrompt = ReadingAssistantPromptFactory.ExpandQuery(retrievalQuestion);
                var expansion = await coordinator.CompleteAsync(new ReadingAssistantJob(
                    tab.LibraryDocument.ContentHash,
                    ReadingTaskKind.QueryExpansion,
                    _translationSettings.Model,
                    expansionPrompt.System,
                    expansionPrompt.User,
                    "none",
                    MaximumOutputTokens: 256), token);
                var evidence = EvidenceRetriever.Search(corpus, question, expansion.Content);
                tab.ReadingEvidence.Clear();
                if (evidence.Count == 0)
                {
                    const string insufficient = "已检索当前文档，但没有找到足够的英文证据。";
                    AppendReadingMessage(tab, new ReadingMessageViewModel(false, insufficient));
                    return new ReadingActionResult(insufficient, "证据不足，未请求生成答案。");
                }

                SetReadingStatus(tab, $"已本地检索 {evidence.Count} 条候选证据，正在生成有据答案...");
                var answerPrompt = ReadingAssistantPromptFactory.AnswerQuestion(
                    question,
                    priorConversation,
                    evidence,
                    _translationSettings.CustomInstruction);
                var answer = await coordinator.CompleteAsync(new ReadingAssistantJob(
                    tab.LibraryDocument.ContentHash,
                    ReadingTaskKind.QuestionAnswer,
                    _translationSettings.Model,
                    answerPrompt.System,
                    answerPrompt.User,
                    TranslationJob.VersionCustomInstruction(_translationSettings.CustomInstruction),
                    Cacheable: false,
                    MaximumOutputTokens: 2_500), token);
                var validation = CitationValidator.Validate(answer.Content, evidence);
                if (!validation.IsValid)
                {
                    AppendReadingMessage(tab, new ReadingMessageViewModel(false, validation.Message));
                    return new ReadingActionResult(validation.Message, "答案未通过本地引用验证。");
                }

                foreach (var item in validation.CitedEvidence)
                {
                    tab.ReadingEvidence.Add(new ReadingEvidenceViewModel(item));
                }

                AppendReadingMessage(tab, new ReadingMessageViewModel(false, answer.Content));
                return new ReadingActionResult(answer.Content, $"回答已通过本地反查 · {validation.CitedEvidence.Count} 条证据。");
            });
    }

    private void CancelReadingButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            tab.CancelReadingRequest();
            SetReadingStatus(tab, "正在取消 AI 阅读任务...");
        }
    }

    private void ClearReadingConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        tab.CancelReadingRequest();
        tab.ReadingMessages.Clear();
        tab.ReadingEvidence.Clear();
        tab.ReadingQuestion = string.Empty;
        ReadingQuestionTextBox.Clear();
        SetReadingStatus(tab, "当前文献问答上下文已清空。");
    }

    private void ReadingQuestionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            tab.ReadingQuestion = ReadingQuestionTextBox.Text;
        }
    }

    private async void NavigateEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReadingEvidenceViewModel item } || ActiveTab is not { } tab)
        {
            return;
        }

        tab.LastKnownPageIndex = Math.Clamp(item.Evidence.PageIndex, 0, tab.Document.PageCount - 1);
        if (tab.BilingualDisplayMode == BilingualDisplayMode.Pdf)
        {
            NavigateToPage(tab.LastKnownPageIndex);
        }
        else
        {
            await RefreshBilingualViewAsync(tab, tab.LastKnownPageIndex);
        }

        StatusText.Text = $"已定位证据 {item.CitationId} · {item.PageLabel} · {item.SectionTitle}";
    }

    private async Task<DocumentCorpus> GetReadingCorpusAsync(
        ReaderTabSession tab,
        CancellationToken cancellationToken)
    {
        if (tab.ReadingCorpus is { } cached)
        {
            return cached;
        }

        var progress = new Progress<int>(completed =>
            SetReadingStatus(tab, $"正在提取当前文献文本 {completed}/{tab.Document.PageCount} 页..."));
        var corpus = await DocumentCorpusBuilder.BuildAsync(
            tab.Document,
            tab.OutlineItems.ToArray(),
            progress,
            cancellationToken);
        if (ReferenceEquals(ActiveTab, tab) && !tab.IsClosing)
        {
            tab.ReadingCorpus = corpus;
        }

        return corpus;
    }

    private async Task<bool> ExecuteReadingActionAsync(
        ReaderTabSession tab,
        string initialStatus,
        Func<ReadingAssistantCoordinator, CancellationToken, Task<ReadingActionResult>> action)
    {
        if (_readingAssistantCoordinator is not { } coordinator)
        {
            SetReadingStatus(tab, "尚未配置可用的 API Key；请先打开翻译服务设置。");
            return false;
        }

        var request = tab.BeginReadingRequest();
        ReadingCancelButton.IsEnabled = true;
        AskReadingQuestionButton.IsEnabled = false;
        UpdateReadingAssistantControls(tab);
        SetReadingStatus(tab, initialStatus);
        try
        {
            var result = await action(coordinator, request.Token);
            request.Token.ThrowIfCancellationRequested();
            tab.ReadingAssistantOutput = result.Output;
            SetReadingStatus(tab, result.Status);
            if (ReferenceEquals(ActiveTab, tab))
            {
                ReadingOutputTextBox.Text = result.Output;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            if (!tab.IsClosing && tab.IsCurrentReadingRequest(request))
            {
                SetReadingStatus(tab, "AI 阅读任务已取消。");
            }
        }
        catch (TimeoutException exception)
        {
            if (!tab.IsClosing)
            {
                SetReadingStatus(tab, exception.Message);
            }
        }
        catch (TranslationProviderException exception)
        {
            if (!tab.IsClosing)
            {
                SetReadingStatus(tab, exception.Message);
            }
        }
        catch (HttpRequestException)
        {
            if (!tab.IsClosing)
            {
                SetReadingStatus(tab, "无法连接 AI 服务，请检查网络、Base URL 或代理设置。");
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or
            PdfiumException or InvalidOperationException or ArgumentException)
        {
            if (!tab.IsClosing)
            {
                SetReadingStatus(tab, $"AI 阅读任务失败：{exception.Message}");
            }
        }
        catch (ObjectDisposedException) when (tab.IsClosing)
        {
        }
        finally
        {
            var wasCurrent = tab.IsCurrentReadingRequest(request);
            tab.CompleteReadingRequest(request);
            if (wasCurrent && !tab.IsClosing && ReferenceEquals(ActiveTab, tab))
            {
                ReadingCancelButton.IsEnabled = false;
                UpdateReadingAssistantControls(tab);
            }
        }

        return false;
    }

    private void RestoreReadingAssistantPanel(ReaderTabSession tab)
    {
        ReadingOutputTextBox.Text = tab.ReadingAssistantOutput;
        ReadingQuestionTextBox.Text = tab.ReadingQuestion;
        ReadingStatusText.Text = _translationUnavailableMessage ?? tab.ReadingAssistantStatus;
        ReadingCancelButton.IsEnabled = tab.ReadingRequest is not null;
        UpdateReadingAssistantContext(tab);
    }

    private void UpdateReadingAssistantContext(ReaderTabSession tab)
    {
        if (!ReferenceEquals(ActiveTab, tab))
        {
            return;
        }

        var hasSelection = tab.LastTranslationInput.Length > 0 && tab.TranslationPageIndex >= 0;
        ReadingSelectionLabel.Text = hasSelection
            ? $"当前 PDF 原文选区 · 第 {tab.TranslationPageIndex + 1} 页 · {tab.LastTranslationInput.Length:N0} 字符"
            : "当前 PDF 原文选区 · 未选择";
        ReadingSelectionPreview.Text = hasSelection
            ? tab.LastTranslationInput
            : "请回到主窗口，在 PDF 原文上拖选要解释的文字。";

        var pageIndex = Math.Clamp(tab.LastKnownPageIndex, 0, tab.Document.PageCount - 1);
        var section = DocumentCorpusBuilder.ResolveSection(
            tab.Document.PageCount,
            tab.OutlineItems.ToArray(),
            pageIndex);
        var isPageFallback = section.Title.Contains("PDF 无目录", StringComparison.Ordinal);
        ReadingSectionScopeText.Text = isPageFallback
            ? $"总结范围：PDF 第 {pageIndex + 1} 页。此 PDF 没有目录，因此“章节总结”明确降级为当前页总结。"
            : $"总结范围：{FormatSectionRange(section)}。范围由主窗口当前阅读页和 PDF 目录共同确定。";
        SummarizeCurrentSectionButton.Content = isPageFallback ? "总结当前 PDF 页" : "总结当前页所属章节";
        UpdateReadingAssistantControls(tab);
    }

    private void UpdateReadingAssistantControls(ReaderTabSession tab)
    {
        if (!ReferenceEquals(ActiveTab, tab))
        {
            return;
        }

        var ready = _readingAssistantCoordinator is not null && tab.ReadingRequest is null;
        var hasSelection = tab.LastTranslationInput.Length > 0 && tab.TranslationPageIndex >= 0;
        ExplainPdfSelectionButton.IsEnabled = ready && hasSelection;
        TranslateExplainPdfSelectionButton.IsEnabled = ready && hasSelection;
        SummarizeCurrentSectionButton.IsEnabled = ready;
        SummarizeWholeDocumentButton.IsEnabled = ready;
        AskReadingQuestionButton.IsEnabled = ready;
    }

    private static string FormatSectionRange(DocumentSection section)
    {
        var startPage = section.StartPageIndex + 1;
        var endPage = section.EndPageIndexExclusive;
        return startPage == endPage
            ? $"{section.Title} · PDF 第 {startPage} 页"
            : $"{section.Title} · PDF 第 {startPage}–{endPage} 页";
    }

    private void SetReadingStatus(ReaderTabSession tab, string status)
    {
        tab.ReadingAssistantStatus = status;
        if (ReferenceEquals(ActiveTab, tab))
        {
            ReadingStatusText.Text = status;
        }
    }

    private static void AppendReadingMessage(ReaderTabSession tab, ReadingMessageViewModel message)
    {
        while (tab.ReadingMessages.Count >= 20)
        {
            tab.ReadingMessages.RemoveAt(0);
        }

        tab.ReadingMessages.Add(message);
    }

    private void TranslationSourceTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            tab.TranslationSourceText = TranslationSourceTextBox.Text;
        }
    }

    private async void PdfTextSelectionLayer_SelectionCompleted(object sender, PdfTextSelectionEventArgs e)
    {
        if (ActiveTab is not { } tab || e.PageIndex < 0 || e.PageIndex >= tab.Document.PageCount)
        {
            return;
        }

        tab.LastKnownPageIndex = e.PageIndex;
        tab.TranslationPageIndex = e.PageIndex;
        tab.TranslationSourceText = e.PageText;
        tab.LastTranslationGranularity = TranslationGranularity.Selection;
        tab.LastTranslationInput = e.SelectedText;

        TranslationSourceTextBox.Text = e.PageText;
        var start = Math.Clamp(e.SelectionStart, 0, TranslationSourceTextBox.Text.Length);
        var length = Math.Clamp(e.SelectionLength, 0, TranslationSourceTextBox.Text.Length - start);
        TranslationSourceTextBox.Select(start, length);
        SelectedSourcePreview.Text = e.SelectedText;
        TranslationPageLabel.Text = $"第 {e.PageIndex + 1} 页 · 已选 {e.SelectedText.Length:N0} 个字符";
        UpdateReadingAssistantContext(tab);
        SetTranslationStatus(tab, "已从 PDF 原文选取，正在翻译…", retryEnabled: false);
        await TranslateTextAsync(tab, TranslationGranularity.Selection, e.SelectedText);
    }

    private void CancelTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            tab.CancelTranslationRequest();
            SetTranslationStatus(tab, "正在取消翻译...");
        }
    }

    private async void RetryTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { LastTranslationInput.Length: > 0 } tab)
        {
            await TranslateTextAsync(tab, tab.LastTranslationGranularity, tab.LastTranslationInput);
        }
    }

    private async Task LoadTranslationSourcePageAsync(ReaderTabSession tab, bool force)
    {
        var pageIndex = Math.Clamp(tab.LastKnownPageIndex, 0, tab.Document.PageCount - 1);
        if (!force && tab.TranslationPageIndex == pageIndex && tab.TranslationSourceText.Length > 0)
        {
            RestoreTranslationPanel(tab);
            return;
        }

        var token = tab.TranslationActivity.Token;
        SetTranslationStatus(tab, $"正在提取第 {pageIndex + 1} 页文字...");
        try
        {
            var page = await Task.Run(
                async () => await tab.Document.ExtractPageTextAsync(pageIndex, token),
                token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ActiveTab, tab))
            {
                return;
            }

            tab.TranslationPageIndex = pageIndex;
            tab.TranslationSourceText = page.Text;
            TranslationSourceTextBox.Text = page.Text;
            TranslationPageLabel.Text = $"第 {pageIndex + 1} 页 · 文字层已就绪";
            SetTranslationStatus(tab, page.Text.Length == 0 ? "该页没有可选择的文本层。" : "请直接在 PDF 原文上拖选要翻译的文字。", false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is PdfiumException or InvalidOperationException)
        {
            SetTranslationStatus(tab, $"页面文字提取失败：{exception.Message}");
        }
    }

    private async Task TranslateTextAsync(
        ReaderTabSession tab,
        TranslationGranularity granularity,
        string source)
    {
        if (_translationCoordinator is not { } coordinator)
        {
            SetTranslationStatus(tab, "尚未配置可用的 API Key；请先打开设置。", retryEnabled: false);
            return;
        }

        if (source.Length > 100_000)
        {
            SetTranslationStatus(tab, "待译文字超过 100,000 字符，请缩小选区。", retryEnabled: false);
            return;
        }

        var request = tab.BeginTranslationRequest();
        CancelTranslationButton.IsEnabled = true;
        RetryTranslationButton.IsEnabled = false;
        SetTranslationStatus(tab, "正在翻译...", retryEnabled: false);
        try
        {
            var context = granularity == TranslationGranularity.Page
                ? string.Empty
                : CreateBoundedContext(tab.TranslationSourceText, source);
            var glossarySnapshot = await _glossaryStore.GetSnapshotAsync(request.Token);
            var glossaryConstraints = GlossaryConstraintResolver.Resolve(glossarySnapshot, source, context);
            var job = new TranslationJob(
                tab.LibraryDocument.ContentHash,
                source,
                _translationSettings.Model,
                granularity,
                context,
                glossaryConstraints.Terminology,
                GlossaryVersion: glossaryConstraints.Version,
                CustomInstructionVersion: TranslationJob.VersionCustomInstruction(_translationSettings.CustomInstruction),
                CustomInstruction: _translationSettings.CustomInstruction);
            var result = await coordinator.TranslateAsync(job, request.Token);
            request.Token.ThrowIfCancellationRequested();
            tab.TranslationMachineOutputText = result.Translation;
            tab.TranslationOutputText = result.Translation;
            tab.TranslationStatus = result.IsCacheHit
                ? $"缓存命中 · {result.Model} · 术语 {glossaryConstraints.Terminology.Count}"
                : $"翻译完成 · {result.Model} · 术语 {glossaryConstraints.Terminology.Count}";
            if (ReferenceEquals(ActiveTab, tab))
            {
                TranslationOutputTextBox.Text = result.Translation;
                TranslationStateText.Text = tab.TranslationStatus;
                RetryTranslationButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (tab.IsCurrentTranslationRequest(request))
            {
                SetTranslationStatus(tab, "翻译已取消。", retryEnabled: true);
            }
        }
        catch (TimeoutException exception)
        {
            if (tab.IsCurrentTranslationRequest(request))
            {
                SetTranslationStatus(tab, exception.Message, retryEnabled: true);
            }
        }
        catch (TranslationProviderException exception)
        {
            if (tab.IsCurrentTranslationRequest(request))
            {
                SetTranslationStatus(tab, exception.Message, retryEnabled: true);
            }
        }
        catch (HttpRequestException)
        {
            if (tab.IsCurrentTranslationRequest(request))
            {
                SetTranslationStatus(tab, "无法连接翻译服务，请检查网络、Base URL 或代理设置。", retryEnabled: true);
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
            if (tab.IsCurrentTranslationRequest(request))
            {
                SetTranslationStatus(tab, $"本地翻译数据处理失败：{exception.Message}", retryEnabled: true);
            }
        }
        finally
        {
            var wasCurrent = tab.IsCurrentTranslationRequest(request);
            tab.CompleteTranslationRequest(request);
            if (wasCurrent && ReferenceEquals(ActiveTab, tab))
            {
                CancelTranslationButton.IsEnabled = false;
            }
        }
    }

    private static string CreateBoundedContext(string pageText, string source)
    {
        if (string.IsNullOrWhiteSpace(pageText) || string.Equals(pageText.Trim(), source.Trim(), StringComparison.Ordinal))
        {
            return string.Empty;
        }

        const int maximumContextCharacters = 4_000;
        return pageText.Length <= maximumContextCharacters
            ? pageText
            : pageText[..maximumContextCharacters];
    }

    private void CaptureTranslationPanel(ReaderTabSession tab)
    {
        if (!ReferenceEquals(ActiveTab, tab))
        {
            return;
        }

        tab.TranslationSourceText = TranslationSourceTextBox.Text;
        tab.TranslationOutputText = TranslationOutputTextBox.Text;
        tab.TranslationStatus = TranslationStateText.Text;
    }

    private void TranslationOutputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        tab.TranslationOutputText = TranslationOutputTextBox.Text;
        LearnTermButton.IsEnabled = tab.LastTranslationInput.Length > 0 &&
            TranslationOutputTextBox.Text.Trim().Length > 0;
    }

    private async void LearnTermButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { LastTranslationInput.Length: > 0 } tab)
        {
            return;
        }

        var english = TranslationSourceTextBox.SelectedText.Trim();
        if (english.Length == 0 && tab.LastTranslationInput.Length <= 256)
        {
            english = tab.LastTranslationInput.Trim();
        }

        var chinese = TranslationOutputTextBox.SelectedText.Trim();
        if (chinese.Length == 0 && TranslationOutputTextBox.Text.Trim().Length <= 256)
        {
            chinese = TranslationOutputTextBox.Text.Trim();
        }

        if (english.Length is < 1 or > 256 || chinese.Length is < 1 or > 256)
        {
            MessageBox.Show(
                this,
                "请分别在原文和编辑后的译文中选择要确认的短语（每项不超过 256 个字符）。",
                "选择术语对",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var snapshot = await _glossaryStore.GetSnapshotAsync();
            var personal = snapshot.PersonalGlossary;
            var dialog = new GlossaryTermDialog(personal, initialEnglish: english, initialChinese: chinese)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.ResultTerm is not { } term)
            {
                return;
            }

            await _glossaryStore.SaveTermAsync(term);
            SetTranslationStatus(tab, $"已确认个人术语：{term.English} → {term.PreferredChinese}", retryEnabled: true);
        }
        catch (Exception exception) when (exception is ArgumentException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            ShowLibraryOperationError("无法保存个人术语", exception);
        }
    }

    private void RestoreTranslationPanel(ReaderTabSession tab)
    {
        TranslationSourceTextBox.Text = tab.TranslationSourceText;
        TranslationOutputTextBox.Text = tab.TranslationOutputText;
        SelectedSourcePreview.Text = tab.LastTranslationInput.Length == 0
            ? "请在 PDF 页面中选择要翻译的文字…"
            : tab.LastTranslationInput;
        TranslationStateText.Text = _translationUnavailableMessage ?? tab.TranslationStatus;
        TranslationPageLabel.Text = tab.TranslationPageIndex < 0
            ? "尚未选择原文"
            : $"第 {tab.TranslationPageIndex + 1} 页 · PDF 原文选区";
        CancelTranslationButton.IsEnabled = tab.TranslationRequest is not null;
        RetryTranslationButton.IsEnabled = tab.LastTranslationInput.Length > 0;
        LearnTermButton.IsEnabled = tab.LastTranslationInput.Length > 0 && tab.TranslationOutputText.Length > 0;
    }

    private void SetTranslationStatus(
        ReaderTabSession tab,
        string status,
        bool retryEnabled = false)
    {
        tab.TranslationStatus = status;
        if (ReferenceEquals(ActiveTab, tab))
        {
            TranslationStateText.Text = status;
            RetryTranslationButton.IsEnabled = retryEnabled && tab.LastTranslationInput.Length > 0;
        }
    }

    private async Task OpenLibraryDocumentAsync(LibraryDocument libraryDocument)
    {
        var existing = OpenTabs.FirstOrDefault(tab => tab.LibraryDocument.Id == libraryDocument.Id);
        if (existing is not null)
        {
            await ActivateTabAsync(existing);
            StatusText.Text = $"已切换到：{existing.Title}";
            return;
        }

        if (!_documentsOpening.Add(libraryDocument.Id))
        {
            StatusText.Text = "该文献正在打开...";
            return;
        }

        ReaderTabSession? session = null;
        StatusText.Text = "正在打开 PDF...";

        try
        {
            var filePath = _library.GetManagedFilePath(libraryDocument);
            session = new ReaderTabSession(libraryDocument, PdfiumDocument.Open(filePath));
            var outline = await session.Document.GetOutlineAsync(session.Lifetime.Token);
            foreach (var item in outline)
            {
                session.OutlineItems.Add(item);
            }

            await ReloadAnnotationsAsync(session);

            OpenTabs.Add(session);
            await ActivateTabAsync(session);
            StatusText.Text = $"{libraryDocument.Title} · {session.Document.PageCount} 页 · PDFium 按需渲染";
            session = null;
        }
        catch (Exception exception) when (
            exception is PdfiumException or System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "打开失败";
            MessageBox.Show(this, exception.Message, "无法打开 PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            session?.Dispose();
            _documentsOpening.Remove(libraryDocument.Id);
        }
    }

    private async void ReaderTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingTabSelection && ReaderTabsList.SelectedItem is ReaderTabSession tab)
        {
            await ActivateTabAsync(tab);
        }
    }

    private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: ReaderTabSession tab })
        {
            await CloseTabAsync(tab);
        }
    }

    private async Task ActivateTabAsync(ReaderTabSession tab)
    {
        if (tab.IsClosing || !OpenTabs.Contains(tab))
        {
            return;
        }

        if (ReferenceEquals(ActiveTab, tab))
        {
            SelectTabHeader(tab);
            return;
        }

        var switchVersion = ++_tabSwitchVersion;
        var previous = ActiveTab;
        _readingPositionTimer.Stop();
        if (previous is not null)
        {
            CaptureReadingPosition(previous);
            CaptureTranslationPanel(previous);
            previous.Deactivate();
        }

        ActiveTab = tab;
        if (_readingAssistantWindow is { } readingWindow)
        {
            readingWindow.Title = $"AI 阅读辅助 · {tab.Title}";
        }
        SelectTabHeader(tab);
        UpdateReaderVisibility();
        RestoreTranslationPanel(tab);
        RestoreReadingAssistantPanel(tab);
        ApplyBilingualDisplayMode(tab);
        ApplyAnnotationFilter(tab);
        CancelFullTranslationButton.IsEnabled = tab.IsFullTranslationActive;

        if (previous is not null)
        {
            try
            {
                await PersistReadingPositionAsync(previous);
            }
            catch (Exception exception)
            {
                StatusText.Text = $"阅读位置保存失败：{exception.Message}";
            }
        }

        if (switchVersion == _tabSwitchVersion && ReferenceEquals(ActiveTab, tab))
        {
            await RestoreReadingPositionAsync(tab);
            if (tab.TranslationPageIndex < 0)
            {
                await LoadTranslationSourcePageAsync(tab, force: false);
            }

            if (tab.BilingualDisplayMode != BilingualDisplayMode.Pdf)
            {
                await RefreshBilingualViewAsync(tab, tab.LastKnownPageIndex);
            }
        }
    }

    private async Task CloseTabAsync(ReaderTabSession tab)
    {
        if (tab.IsClosing || !OpenTabs.Contains(tab))
        {
            return;
        }

        var index = OpenTabs.IndexOf(tab);
        var wasActive = ReferenceEquals(ActiveTab, tab);
        _readingPositionTimer.Stop();
        if (wasActive)
        {
            CaptureReadingPosition(tab);
            CaptureTranslationPanel(tab);
        }

        tab.Deactivate();
        try
        {
            await PersistReadingPositionAsync(tab);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"阅读位置保存失败：{exception.Message}";
        }

        _changingTabSelection = true;
        try
        {
            OpenTabs.Remove(tab);
            if (wasActive)
            {
                ActiveTab = null;
                ReaderTabsList.SelectedItem = null;
            }
        }
        finally
        {
            _changingTabSelection = false;
        }

        RemoveCachedPages(tab);
        tab.Dispose();

        if (wasActive && OpenTabs.Count > 0)
        {
            var next = OpenTabs[Math.Min(index, OpenTabs.Count - 1)];
            await ActivateTabAsync(next);
        }
        else
        {
            UpdateReaderVisibility();
        }
    }

    private async Task RestoreReadingPositionAsync(ReaderTabSession tab)
    {
        if (tab.Pages.Count == 0 || !ReferenceEquals(ActiveTab, tab))
        {
            return;
        }

        _restoringReadingPosition = true;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!ReferenceEquals(ActiveTab, tab))
            {
                return;
            }

            var pageIndex = Math.Clamp(tab.LastKnownPageIndex, 0, tab.Pages.Count - 1);
            PagesList.ScrollIntoView(tab.Pages[pageIndex]);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (ReferenceEquals(ActiveTab, tab) &&
                tab.LastKnownScrollOffset > 0 &&
                FindVisualChild<ScrollViewer>(PagesList) is { } scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(tab.LastKnownScrollOffset);
            }
        }
        finally
        {
            if (ReferenceEquals(ActiveTab, tab))
            {
                _restoringReadingPosition = false;
            }
        }
    }

    private async void PageContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: PdfPageViewModel page } ||
            FindTab(page.TabId) is not { } tab ||
            !ReferenceEquals(ActiveTab, tab) ||
            tab.IsClosing)
        {
            return;
        }

        var cacheKey = new PageCacheKey(tab.Id, page.PageIndex, PreviewScale);
        if (_pageCache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            page.SetImage(CreateBitmap(cached));
            await EnsurePageTextAsync(tab, page);
            return;
        }

        if (!page.TryBeginLoading())
        {
            await EnsurePageTextAsync(tab, page);
            return;
        }

        CancelPageLoad(tab, page.PageIndex);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(tab.Lifetime.Token);
        tab.PageLoads[page.PageIndex] = cancellation;

        try
        {
            var rendered = await Task.Run(
                async () => await tab.Document.RenderPageAsync(
                    page.PageIndex,
                    new PdfRenderRequest(PreviewScale),
                    cancellation.Token),
                cancellation.Token);

            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ActiveTab, tab) || tab.IsClosing)
            {
                return;
            }

            _pageCache.Set(cacheKey, rendered);
            page.SetImage(CreateBitmap(rendered));
            await EnsurePageTextAsync(tab, page, cancellation.Token);
            StatusText.Text = $"第 {page.PageIndex + 1} 页已渲染 · 缓存 {_pageCache.CurrentBytes / 1024d / 1024d:F1} MB";
        }
        catch (OperationCanceledException)
        {
            page.CancelLoading();
        }
        catch (Exception exception) when (exception is PdfiumException or InvalidOperationException)
        {
            page.SetFailure("页面渲染失败");
            if (ReferenceEquals(ActiveTab, tab))
            {
                StatusText.Text = exception.Message;
            }
        }
        finally
        {
            if (tab.PageLoads.TryGetValue(page.PageIndex, out var active) && active == cancellation)
            {
                tab.PageLoads.Remove(page.PageIndex);
            }

            cancellation.Dispose();
        }
    }

    private async Task EnsurePageTextAsync(
        ReaderTabSession tab,
        PdfPageViewModel page,
        CancellationToken cancellationToken = default)
    {
        if (!page.TryBeginTextLoading())
        {
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(tab.Lifetime.Token, cancellationToken);
            var pageText = await Task.Run(
                async () => await tab.Document.ExtractPageTextAsync(page.PageIndex, linked.Token),
                linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!tab.IsClosing)
            {
                page.SetPageText(pageText);
            }
        }
        catch (OperationCanceledException)
        {
            page.CancelTextLoading();
        }
        catch (Exception exception) when (exception is PdfiumException or InvalidOperationException)
        {
            page.CancelTextLoading();
            if (ReferenceEquals(ActiveTab, tab))
            {
                StatusText.Text = $"第 {page.PageIndex + 1} 页文字层载入失败：{exception.Message}";
            }
        }
    }

    private async void ThumbnailContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: PdfThumbnailViewModel thumbnail } ||
            FindTab(thumbnail.TabId) is not { } tab ||
            !ReferenceEquals(ActiveTab, tab) ||
            tab.IsClosing)
        {
            return;
        }

        var cacheKey = new PageCacheKey(tab.Id, thumbnail.PageIndex, ThumbnailScale);
        if (_thumbnailCache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            thumbnail.SetImage(CreateBitmap(cached));
            return;
        }

        if (!thumbnail.TryBeginLoading())
        {
            return;
        }

        CancelThumbnailLoad(tab, thumbnail.PageIndex);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(tab.Lifetime.Token);
        tab.ThumbnailLoads[thumbnail.PageIndex] = cancellation;

        try
        {
            var rendered = await Task.Run(
                async () => await tab.Document.RenderPageAsync(
                    thumbnail.PageIndex,
                    new PdfRenderRequest(ThumbnailScale),
                    cancellation.Token),
                cancellation.Token);

            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ActiveTab, tab) || tab.IsClosing)
            {
                return;
            }

            _thumbnailCache.Set(cacheKey, rendered);
            thumbnail.SetImage(CreateBitmap(rendered));
        }
        catch (OperationCanceledException)
        {
            thumbnail.CancelLoading();
        }
        catch (Exception exception) when (exception is PdfiumException or InvalidOperationException)
        {
            thumbnail.SetFailure();
            if (ReferenceEquals(ActiveTab, tab))
            {
                StatusText.Text = exception.Message;
            }
        }
        finally
        {
            if (tab.ThumbnailLoads.TryGetValue(thumbnail.PageIndex, out var active) && active == cancellation)
            {
                tab.ThumbnailLoads.Remove(thumbnail.PageIndex);
            }

            cancellation.Dispose();
        }
    }

    private void PageContainer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: PdfPageViewModel page } || FindTab(page.TabId) is not { } tab)
        {
            return;
        }

        CancelPageLoad(tab, page.PageIndex);
        page.CancelLoading();
        page.CancelTextLoading();
        page.ReleaseImage();
    }

    private void ThumbnailContainer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: PdfThumbnailViewModel thumbnail } ||
            FindTab(thumbnail.TabId) is not { } tab)
        {
            return;
        }

        CancelThumbnailLoad(tab, thumbnail.PageIndex);
        thumbnail.CancelLoading();
        thumbnail.ReleaseImage();
    }

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailList.SelectedItem is PdfThumbnailViewModel thumbnail)
        {
            NavigateToPage(thumbnail.PageIndex);
        }
    }

    private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is PdfOutlineItem { PageIndex: int pageIndex })
        {
            NavigateToPage(pageIndex);
        }
    }

    private void NavigateToPage(int pageIndex)
    {
        if (ActiveTab is not { } tab || (uint)pageIndex >= (uint)tab.Pages.Count)
        {
            return;
        }

        PagesList.ScrollIntoView(tab.Pages[pageIndex]);
        tab.LastKnownPageIndex = pageIndex;
        UpdateReadingAssistantContext(tab);
        StatusText.Text = $"跳转到第 {pageIndex + 1} 页";
    }

    private void PagesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoringReadingPosition || ActiveTab is not { } tab || e.VerticalChange == 0)
        {
            return;
        }

        var previousPageIndex = tab.LastKnownPageIndex;
        tab.LastKnownPageIndex = FindFirstVisiblePageIndex(tab) ?? tab.LastKnownPageIndex;
        tab.LastKnownScrollOffset = Math.Max(0, e.VerticalOffset);
        if (tab.LastKnownPageIndex != previousPageIndex)
        {
            UpdateReadingAssistantContext(tab);
        }
        _readingPositionTimer.Stop();
        _readingPositionTimer.Start();
    }

    private void PagesList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (FindVisualChild<ScrollViewer>(PagesList) is not { } scrollViewer)
        {
            return;
        }

        var movement = PdfScrollWheel.GetPixelMovement(e.Delta, scrollViewer.ViewportHeight);
        var target = Math.Clamp(
            scrollViewer.VerticalOffset + movement,
            0,
            Math.Max(0, scrollViewer.ScrollableHeight));
        scrollViewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private async void ReadingPositionTimer_Tick(object? sender, EventArgs e)
    {
        _readingPositionTimer.Stop();
        if (ActiveTab is not { } tab)
        {
            return;
        }

        try
        {
            CaptureReadingPosition(tab);
            await PersistReadingPositionAsync(tab);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"阅读位置保存失败：{exception.Message}";
        }
    }

    private void CaptureReadingPosition(ReaderTabSession tab)
    {
        if (!ReferenceEquals(ActiveTab, tab))
        {
            return;
        }

        tab.LastKnownPageIndex = FindFirstVisiblePageIndex(tab) ?? tab.LastKnownPageIndex;
        if (FindVisualChild<ScrollViewer>(PagesList) is { } scrollViewer)
        {
            tab.LastKnownScrollOffset = Math.Max(0, scrollViewer.VerticalOffset);
        }
    }

    private int? FindFirstVisiblePageIndex(ReaderTabSession tab)
    {
        PdfPageViewModel? closest = null;
        var closestDistance = double.MaxValue;

        for (var index = 0; index < tab.Pages.Count; index++)
        {
            if (PagesList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            try
            {
                var top = container.TranslatePoint(new Point(0, 0), PagesList).Y;
                var bottom = top + container.ActualHeight;
                if (bottom <= 0 || top >= PagesList.ActualHeight)
                {
                    continue;
                }

                var distance = Math.Abs(top);
                if (distance < closestDistance && container.DataContext is PdfPageViewModel page)
                {
                    closest = page;
                    closestDistance = distance;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return closest?.PageIndex;
    }

    private async Task PersistReadingPositionAsync(ReaderTabSession tab)
    {
        if (!_libraryInitialized || tab.IsClosing)
        {
            return;
        }

        var current = tab.LibraryDocument;
        await _library.UpdateReadingPositionAsync(
            current.Id,
            tab.LastKnownPageIndex,
            tab.LastKnownScrollOffset);

        var updated = current with
        {
            LastOpenedAtUtc = DateTimeOffset.UtcNow,
            LastPageIndex = tab.LastKnownPageIndex,
            LastScrollOffset = tab.LastKnownScrollOffset
        };
        tab.LibraryDocument = updated;

        var libraryIndex = FindLibraryDocumentIndex(current.Id);
        if (libraryIndex >= 0)
        {
            LibraryDocuments[libraryIndex] = updated;
        }
    }

    private int FindLibraryDocumentIndex(DocumentId documentId)
    {
        for (var index = 0; index < LibraryDocuments.Count; index++)
        {
            if (LibraryDocuments[index].Id == documentId)
            {
                return index;
            }
        }

        return -1;
    }

    private LibraryFolder? GetSelectedFolder()
    {
        if (FolderFilterCombo.SelectedItem is not LibraryFolderFilterOption
            {
                Kind: LibraryFolderFilterKind.Folder,
                FolderId: Guid folderId
            })
        {
            return null;
        }

        return LibraryFolders.FirstOrDefault(folder => folder.Id == folderId);
    }

    private void UpdateFolderManagementButtons()
    {
        var hasSelectedFolder = GetSelectedFolder() is not null;
        RenameFolderButton.IsEnabled = hasSelectedFolder;
        DeleteFolderButton.IsEnabled = hasSelectedFolder;
    }

    private static LibraryDocument? GetContextDocument(object sender)
    {
        if (sender is not MenuItem menuItem ||
            ItemsControl.ItemsControlFromItemContainer(menuItem) is not ContextMenu contextMenu)
        {
            return null;
        }

        return (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as LibraryDocument;
    }

    private void ShowLibraryOperationError(string title, Exception exception)
    {
        StatusText.Text = title;
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SelectTabHeader(ReaderTabSession tab)
    {
        if (ReferenceEquals(ReaderTabsList.SelectedItem, tab))
        {
            return;
        }

        _changingTabSelection = true;
        try
        {
            ReaderTabsList.SelectedItem = tab;
            ReaderTabsList.ScrollIntoView(tab);
        }
        finally
        {
            _changingTabSelection = false;
        }
    }

    private void UpdateReaderVisibility()
    {
        var hasActiveTab = ActiveTab is not null;
        EmptyState.Visibility = hasActiveTab ? Visibility.Collapsed : Visibility.Visible;
        DocumentReader.Visibility = hasActiveTab ? Visibility.Visible : Visibility.Collapsed;
        OutlineEmptyText.Visibility = ActiveTab?.OutlineItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        Title = hasActiveTab ? $"PaperBridge - {ActiveTab!.Title}" : "PaperBridge";
        if (!hasActiveTab)
        {
            PagesList.Visibility = Visibility.Visible;
            BilingualViewHost.Visibility = Visibility.Collapsed;
            StatusText.Text = $"文献库已就绪 · {LibraryDocuments.Count} 篇";
            TranslationSourceTextBox.Clear();
            TranslationOutputTextBox.Clear();
            SelectedSourcePreview.Text = "请在 PDF 页面中选择要翻译的文字…";
            TranslationPageLabel.Text = "尚未选择原文";
            TranslationStateText.Text = _translationCoordinator is null
                ? _translationUnavailableMessage ?? "尚未配置 API Key；请打开设置。"
                : "请在 PDF 原文上拖选要翻译的文字";
            CancelTranslationButton.IsEnabled = false;
            RetryTranslationButton.IsEnabled = false;
            LearnTermButton.IsEnabled = false;
            CancelFullTranslationButton.IsEnabled = false;
            ReadingOutputTextBox.Clear();
            ReadingQuestionTextBox.Clear();
            ReadingSelectionLabel.Text = "当前 PDF 原文选区 · 未选择";
            ReadingSelectionPreview.Text = "请回到主窗口，在 PDF 原文上拖选要解释的文字。";
            ReadingSectionScopeText.Text = "章节范围：跟随主窗口当前阅读页；正在等待文献。";
            ReadingStatusText.Text = _translationUnavailableMessage ?? "可解释选区、总结或就当前文献提问";
            ReadingCancelButton.IsEnabled = false;
            ExplainPdfSelectionButton.IsEnabled = false;
            TranslateExplainPdfSelectionButton.IsEnabled = false;
            SummarizeCurrentSectionButton.IsEnabled = false;
            SummarizeWholeDocumentButton.IsEnabled = false;
            AskReadingQuestionButton.IsEnabled = false;
            AnnotationList.ItemsSource = null;
            NavigateAnnotationButton.IsEnabled = false;
            EditAnnotationButton.IsEnabled = false;
            DeleteAnnotationButton.IsEnabled = false;
        }
    }

    private ReaderTabSession? FindTab(Guid tabId) => OpenTabs.FirstOrDefault(tab => tab.Id == tabId);

    private static void CancelPageLoad(ReaderTabSession tab, int pageIndex)
    {
        if (tab.PageLoads.Remove(pageIndex, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    private static void CancelThumbnailLoad(ReaderTabSession tab, int pageIndex)
    {
        if (tab.ThumbnailLoads.Remove(pageIndex, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    private void RemoveCachedPages(ReaderTabSession tab)
    {
        for (var pageIndex = 0; pageIndex < tab.Document.PageCount; pageIndex++)
        {
            _pageCache.Remove(new PageCacheKey(tab.Id, pageIndex, PreviewScale));
            _thumbnailCache.Remove(new PageCacheKey(tab.Id, pageIndex, ThumbnailScale));
        }
    }

    private static ImageSource CreateBitmap(PdfRenderedPage page)
    {
        var bitmap = BitmapSource.Create(
            page.PixelWidth,
            page.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            page.Bgra32Pixels,
            page.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private readonly record struct PageCacheKey(Guid TabId, int PageIndex, double Scale);

    private readonly record struct ReadingActionResult(string Output, string Status);
}
