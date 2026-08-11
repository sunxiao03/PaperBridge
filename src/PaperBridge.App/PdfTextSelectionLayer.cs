using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Translation;

namespace PaperBridge.App;

/// <summary>
/// Adds a selectable interaction layer over the PDFium page bitmap.  PDF text
/// coordinates are kept in points and projected to the displayed image, so the
/// page remains visually faithful while selection happens on the original text.
/// </summary>
public sealed class PdfTextSelectionLayer : FrameworkElement
{
    public static readonly DependencyProperty PageTextProperty = DependencyProperty.Register(
        nameof(PageText),
        typeof(PdfPageText),
        typeof(PdfTextSelectionLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPageTextChanged));

    public static readonly RoutedEvent SelectionCompletedEvent = EventManager.RegisterRoutedEvent(
        nameof(SelectionCompleted),
        RoutingStrategy.Bubble,
        typeof(EventHandler<PdfTextSelectionEventArgs>),
        typeof(PdfTextSelectionLayer));

    private int _anchorIndex = -1;
    private int _activeIndex = -1;
    private Point _mouseDownPoint;

    public PdfPageText? PageText
    {
        get => (PdfPageText?)GetValue(PageTextProperty);
        set => SetValue(PageTextProperty, value);
    }

    public event EventHandler<PdfTextSelectionEventArgs> SelectionCompleted
    {
        add => AddHandler(SelectionCompletedEvent, value);
        remove => RemoveHandler(SelectionCompletedEvent, value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _mouseDownPoint = e.GetPosition(this);
        _anchorIndex = FindNearestCharacter(_mouseDownPoint);
        _activeIndex = _anchorIndex;
        if (_anchorIndex >= 0)
        {
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var next = FindNearestCharacter(e.GetPosition(this));
        if (next >= 0 && next != _activeIndex)
        {
            _activeIndex = next;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured || PageText is null || _anchorIndex < 0)
        {
            return;
        }

        var mouseUpPoint = e.GetPosition(this);
        ReleaseMouseCapture();
        if ((mouseUpPoint - _mouseDownPoint).Length < 4)
        {
            ExpandToWord();
        }

        var start = Math.Min(_anchorIndex, _activeIndex);
        var end = Math.Max(_anchorIndex, _activeIndex);
        var selection = PdfPageTextSelection.Resolve(PageText, start, end);
        if (selection.Length > 0)
        {
            RaiseEvent(new PdfTextSelectionEventArgs(
                SelectionCompletedEvent,
                this,
                PageText.PageIndex,
                PageText.Text,
                selection.Text,
                selection.Start,
                selection.Length));
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (PageText is null || _anchorIndex < 0 || _activeIndex < 0)
        {
            return;
        }

        var start = Math.Min(_anchorIndex, _activeIndex);
        var end = Math.Max(_anchorIndex, _activeIndex);
        var fill = new SolidColorBrush(Color.FromArgb(92, 66, 153, 225));
        fill.Freeze();
        for (var index = start; index <= end; index++)
        {
            if (ToDisplayRectangle(PageText.Characters[index].Bounds) is { } rectangle)
            {
                drawingContext.DrawRoundedRectangle(fill, null, rectangle, 1.5, 1.5);
            }
        }
    }

    private static void OnPageTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var layer = (PdfTextSelectionLayer)dependencyObject;
        layer._anchorIndex = -1;
        layer._activeIndex = -1;
    }

    private int FindNearestCharacter(Point point)
    {
        if (PageText is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return -1;
        }

        var bestIndex = -1;
        var bestScore = double.MaxValue;
        for (var index = 0; index < PageText.Characters.Count; index++)
        {
            if (ToDisplayRectangle(PageText.Characters[index].Bounds) is not { } rectangle)
            {
                continue;
            }

            if (rectangle.Contains(point))
            {
                return index;
            }

            var dx = point.X < rectangle.Left ? rectangle.Left - point.X :
                point.X > rectangle.Right ? point.X - rectangle.Right : 0;
            var dy = point.Y < rectangle.Top ? rectangle.Top - point.Y :
                point.Y > rectangle.Bottom ? point.Y - rectangle.Bottom : 0;
            var score = dx * dx + dy * dy * 3;
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private Rect? ToDisplayRectangle(PdfRectangle? bounds)
    {
        if (PageText is null || bounds is null || PageText.WidthInPoints <= 0 || PageText.HeightInPoints <= 0)
        {
            return null;
        }

        var x = bounds.Left / PageText.WidthInPoints * ActualWidth;
        var y = (PageText.HeightInPoints - bounds.Top) / PageText.HeightInPoints * ActualHeight;
        var width = Math.Max(1, bounds.Width / PageText.WidthInPoints * ActualWidth);
        var height = Math.Max(2, bounds.Height / PageText.HeightInPoints * ActualHeight);
        return new Rect(x, y, width, height);
    }

    private void ExpandToWord()
    {
        if (PageText is null || _anchorIndex < 0)
        {
            return;
        }

        static bool IsWordCharacter(PdfTextCharacter character) =>
            character.Text.Any(value => char.IsLetterOrDigit(value) || value is '-' or '_' or '\'');

        var start = _anchorIndex;
        var end = _anchorIndex;
        while (start > 0 && IsWordCharacter(PageText.Characters[start - 1]))
        {
            start--;
        }

        while (end + 1 < PageText.Characters.Count && IsWordCharacter(PageText.Characters[end + 1]))
        {
            end++;
        }

        _anchorIndex = start;
        _activeIndex = end;
    }
}

public sealed class PdfTextSelectionEventArgs : RoutedEventArgs
{
    public PdfTextSelectionEventArgs(
        RoutedEvent routedEvent,
        object source,
        int pageIndex,
        string pageText,
        string selectedText,
        int selectionStart,
        int selectionLength)
        : base(routedEvent, source)
    {
        PageIndex = pageIndex;
        PageText = pageText;
        SelectedText = selectedText;
        SelectionStart = selectionStart;
        SelectionLength = selectionLength;
    }

    public int PageIndex { get; }

    public string PageText { get; }

    public string SelectedText { get; }

    public int SelectionStart { get; }

    public int SelectionLength { get; }
}
