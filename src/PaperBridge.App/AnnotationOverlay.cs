using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using PaperBridge.Application.Annotations;

namespace PaperBridge.App;

public sealed class AnnotationOverlay : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable<AnnotationOverlayItem>),
        typeof(AnnotationOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    private INotifyCollectionChanged? _observedCollection;

    public IEnumerable<AnnotationOverlayItem>? ItemsSource
    {
        get => (IEnumerable<AnnotationOverlayItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ItemsSource is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        foreach (var item in ItemsSource.Where(item =>
                     item.Status is AnnotationAnchorStatus.Valid or AnnotationAnchorStatus.Migrated))
        {
            var color = (Color)ColorConverter.ConvertFromString(item.Color);
            var fill = new SolidColorBrush(color) { Opacity = item.Kind == AnnotationKind.Note ? 0.20 : 0.32 };
            fill.Freeze();
            var penBrush = new SolidColorBrush(color) { Opacity = 0.92 };
            penBrush.Freeze();
            var pen = new Pen(penBrush, item.Kind == AnnotationKind.Underline ? 2.2 : 1.2);
            pen.Freeze();
            foreach (var normalized in item.Rectangles)
            {
                var rectangle = new Rect(
                    normalized.Left * ActualWidth,
                    normalized.Top * ActualHeight,
                    normalized.Width * ActualWidth,
                    normalized.Height * ActualHeight);
                if (item.Kind == AnnotationKind.Underline)
                {
                    var y = rectangle.Bottom - 1;
                    drawingContext.DrawLine(pen, new Point(rectangle.Left, y), new Point(rectangle.Right, y));
                }
                else
                {
                    drawingContext.DrawRectangle(fill, item.Kind == AnnotationKind.Note ? pen : null, rectangle);
                }
            }
        }
    }

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (AnnotationOverlay)dependencyObject;
        if (overlay._observedCollection is not null)
        {
            overlay._observedCollection.CollectionChanged -= overlay.CollectionChanged;
        }

        overlay._observedCollection = e.NewValue as INotifyCollectionChanged;
        if (overlay._observedCollection is not null)
        {
            overlay._observedCollection.CollectionChanged += overlay.CollectionChanged;
        }

        overlay.InvalidateVisual();
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
