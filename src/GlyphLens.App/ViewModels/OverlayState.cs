using GlyphLens.App.Mvvm;

namespace GlyphLens.App.ViewModels;

/// <summary>A recognised text box in frame pixels, for drawing over the preview.</summary>
public sealed record OcrBox(int X, int Y, int Width, int Height, string Text);

/// <summary>
/// The boxes the preview draws over the captured picture, and the frame size they refer to.
/// </summary>
/// <remarks>
/// Boxes are in the pixels of the frame they were recognised in. The preview scales picture and overlay together
/// (one <c>Viewbox</c>), so no DPI or aspect-ratio conversion is needed here.
/// </remarks>
public sealed class OverlayState : ViewModelBase
{
    private IReadOnlyList<OcrBox> _boxes = [];
    private double _frameWidth;
    private double _frameHeight;

    public IReadOnlyList<OcrBox> Boxes
    {
        get => _boxes;
        private set => SetProperty(ref _boxes, value);
    }

    public double FrameWidth
    {
        get => _frameWidth;
        private set => SetProperty(ref _frameWidth, value);
    }

    public double FrameHeight
    {
        get => _frameHeight;
        private set => SetProperty(ref _frameHeight, value);
    }

    public void Set(IReadOnlyList<OcrBox> boxes, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Boxes = boxes;
    }

    public void Clear()
    {
        Boxes = [];
        FrameWidth = 0;
        FrameHeight = 0;
    }
}
