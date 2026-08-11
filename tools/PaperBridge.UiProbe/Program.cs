using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PaperBridge.App;
using PaperBridge.Application.Translation;

namespace PaperBridge.UiProbe;

internal static class Program
{
    private static readonly string ProgressPath = Path.Combine(
        Environment.GetEnvironmentVariable("PAPERBRIDGE_DATA_DIR") ?? Path.GetTempPath(),
        "ui-probe.log");

    [STAThread]
    private static int Main()
    {
        try
        {
            var application = new System.Windows.Application();
            application.Resources["PaperBridgeAccent"] = Color.FromRgb(49, 91, 120);
            application.Resources["PaperBridgeAccentBrush"] = new SolidColorBrush(Color.FromRgb(49, 91, 120));

            var mainWindow = new MainWindow
            {
                Left = -30_000,
                Top = -30_000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            mainWindow.Show();
            Log("Probe: main window created.");
            VerifyBoundedPixelScrolling(mainWindow);
            Log("Probe: bounded pixel scrolling passed.");
            VerifySidebarInteraction(mainWindow);
            Log("Probe: sidebar interaction passed.");
            VerifyReadingAssistantContext(mainWindow);
            Log("Probe: AI context passed.");
            VerifyCustomInstructionGuide(mainWindow);
            Log("Probe: custom instruction guide passed.");
            Log("PaperBridge WPF interaction probe passed.");
            Console.Out.Flush();
            Environment.Exit(0);
            return 0;
        }
        catch (Exception exception)
        {
            Log(exception.ToString());
            Console.Error.WriteLine(exception);
            Console.Error.Flush();
            Environment.Exit(1);
            return 1;
        }
    }

    private static void Log(string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath)!);
        File.AppendAllText(ProgressPath, message + Environment.NewLine);
        Console.WriteLine(message);
    }

    private static void VerifyBoundedPixelScrolling(MainWindow window)
    {
        var documentReader = Find<Grid>(window, "DocumentReader");
        var emptyState = Find<StackPanel>(window, "EmptyState");
        var pages = Find<ListBox>(window, "PagesList");
        documentReader.Visibility = Visibility.Visible;
        emptyState.Visibility = Visibility.Collapsed;
        pages.Visibility = Visibility.Visible;
        BindingOperations.ClearBinding(pages, ItemsControl.ItemsSourceProperty);
        for (var index = 0; index < 30; index++)
        {
            pages.Items.Add(new Border { Height = 900 });
        }

        window.UpdateLayout();
        var scrollViewer = FindVisualChild<ScrollViewer>(pages)
            ?? throw new InvalidOperationException("PagesList ScrollViewer was not created.");
        if (VirtualizingPanel.GetScrollUnit(pages) != ScrollUnit.Pixel)
        {
            throw new InvalidOperationException("PagesList is not configured for pixel scrolling.");
        }

        var handler = typeof(MainWindow).GetMethod(
            "PagesList_PreviewMouseWheel",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "PagesList_PreviewMouseWheel");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -12_000)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = pages
        };
        handler.Invoke(window, [pages, wheel]);
        window.UpdateLayout();
        if (scrollViewer.VerticalOffset is <= 0 or > 84.01)
        {
            throw new InvalidOperationException(
                $"Extreme wheel input moved {scrollViewer.VerticalOffset:F2}px; expected 0–84px.");
        }
    }

    private static void VerifySidebarInteraction(MainWindow window)
    {
        var toggle = Find<Button>(window, "LibrarySidebarToggleButton");
        var column = Find<ColumnDefinition>(window, "LibrarySidebarColumn");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (column.Width.Value != 0)
        {
            throw new InvalidOperationException("Library sidebar did not collapse.");
        }

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (column.Width.Value != 280)
        {
            throw new InvalidOperationException("Library sidebar did not expand.");
        }
    }

    private static void VerifyReadingAssistantContext(MainWindow window)
    {
        var selectionLabel = Find<TextBlock>(window, "ReadingSelectionLabel");
        var scope = Find<TextBlock>(window, "ReadingSectionScopeText");
        var explain = Find<Button>(window, "ExplainPdfSelectionButton");
        if (!selectionLabel.Text.Contains("未选择", StringComparison.Ordinal) ||
            !scope.Text.Contains("章节范围", StringComparison.Ordinal) || explain.IsEnabled)
        {
            throw new InvalidOperationException("AI reading context does not clearly describe its inactive source.");
        }
    }

    private static void VerifyCustomInstructionGuide(MainWindow owner)
    {
        var dialog = new TranslationSettingsDialog(TranslationServiceSettings.Default, hasStoredKey: false)
        {
            Owner = owner,
            Left = -30_000,
            Top = -30_000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        dialog.Show();
        var guide = Find<TextBlock>(dialog, "CustomInstructionGuideText");
        var insert = Find<Button>(dialog, "UseInstructionExampleButton");
        var editor = Find<TextBox>(dialog, "CustomInstructionTextBox");
        var guideText = guide.Text + string.Concat(
            guide.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
        if (!guideText.Contains("隐私", StringComparison.Ordinal) ||
            !guideText.Contains("划词翻译", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Custom instruction guide is incomplete.");
        }

        insert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!editor.Text.Contains("核工程学术中文", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Custom instruction example was not inserted.");
        }

        dialog.Close();
    }

    private static T Find<T>(FrameworkElement root, string name)
        where T : class => root.FindName(name) as T
        ?? throw new InvalidOperationException($"Required element '{name}' was not found as {typeof(T).Name}.");

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
}
