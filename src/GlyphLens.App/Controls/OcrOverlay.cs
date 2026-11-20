using System.Windows;
using System.Windows.Media;
using GlyphLens.App.ViewModels;

namespace GlyphLens.App.Controls;

/// <summary>
/// Draws recognised-text boxes in frame pixels. It is placed in the same <c>Viewbox</c> as the preview image, so picture and
/// boxes are scaled together and always line up, whatever the window size or display scaling.
/// </summary>
public sealed class OcrOverlay : FrameworkElement
{
    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(
        nameof(Boxes),
        typeof(IReadOnlyList<OcrBox>),
        typeof(OcrOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(40, 0x4C, 0x8D, 0xFF)));
    private static readonly Pen Outline = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), 3));

    public IReadOnlyList<OcrBox>? Boxes
    {
        get => (IReadOnlyList<OcrBox>?)GetValue(BoxesProperty);
        set => SetValue(BoxesProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Boxes is not { Count: > 0 } boxes)
        {
            return;
        }

        foreach (var box in boxes)
        {
            drawingContext.DrawRectangle(Fill, Outline, new Rect(box.X, box.Y, box.Width, box.Height));
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Pen pen)
    {
        pen.Brush.Freeze();
        pen.Freeze();
        return pen;
    }
}
