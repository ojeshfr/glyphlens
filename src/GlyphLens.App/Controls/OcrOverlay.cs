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

    // The boxes use the accent token (Themes/Tokens.xaml), read when drawing so a palette change needs no code change.
    private const byte FillAlpha = 40;
    private static readonly Color FallbackAccent = Colors.RoyalBlue;

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

        var accent = TryFindResource("AccentColor") is Color color ? color : FallbackAccent;
        var fill = Freeze(new SolidColorBrush(Color.FromArgb(FillAlpha, accent.R, accent.G, accent.B)));
        var outline = FreezePen(new Pen(new SolidColorBrush(accent), 3));
        foreach (var box in boxes)
        {
            drawingContext.DrawRectangle(fill, outline, new Rect(box.X, box.Y, box.Width, box.Height));
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
