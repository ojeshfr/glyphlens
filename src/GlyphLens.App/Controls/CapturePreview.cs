using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlyphLens.Core.Abstractions;

namespace GlyphLens.App.Controls;

/// <summary>
/// Shows the most recent captured frame from an <see cref="IFrameSource"/>, scaled to fit while preserving aspect ratio.
/// </summary>
/// <remarks>
/// <para>
/// The control polls the frame source once per WPF render tick (<see cref="CompositionTarget.Rendering"/>) rather than
/// reacting to every frame, so it naturally shows "the latest frame" at the display's refresh rate and never builds a
/// backlog. A frame is copied only when its version changed.
/// </para>
/// <para>
/// One <see cref="WriteableBitmap"/> is reused until the frame size changes, so steady-state preview allocates no
/// bitmaps. The bitmap is declared at 96 DPI, so one frame pixel is one device-independent unit and the layout
/// system's <see cref="Stretch.Uniform"/> does all scaling; frame pixels are never reinterpreted as DPI-scaled units.
/// The pixel copy is the only per-frame work done on the UI thread. This is view plumbing, not application logic.
/// </para>
/// </remarks>
public sealed class CapturePreview : Image
{
    public static readonly DependencyProperty FrameSourceProperty = DependencyProperty.Register(
        nameof(FrameSource),
        typeof(IFrameSource),
        typeof(CapturePreview),
        new PropertyMetadata(null, OnFrameSourceChanged));

    private WriteableBitmap? _bitmap;
    private long _shownVersion = -1;
    private bool _renderingHooked;

    public CapturePreview()
    {
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.Both;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        Loaded += (_, _) => HookRendering();
        Unloaded += (_, _) => UnhookRendering();
    }

    public IFrameSource? FrameSource
    {
        get => (IFrameSource?)GetValue(FrameSourceProperty);
        set => SetValue(FrameSourceProperty, value);
    }

    private static void OnFrameSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CapturePreview)d).Clear();

    private void HookRendering()
    {
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }
    }

    private void UnhookRendering()
    {
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }

        Clear();
    }

    private void Clear()
    {
        _bitmap = null;
        _shownVersion = -1;
        Source = null;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var source = FrameSource;
        if (source is null)
        {
            return;
        }

        if (source.LatestMetadata is null)
        {
            if (_bitmap is not null)
            {
                Clear(); // capture stopped: do not leave a stale picture that looks live
            }

            return;
        }

        if (source.Version == _shownVersion)
        {
            return;
        }

        if (source.TryAcquireLatest() is not { } latest)
        {
            return;
        }

        using (latest.Frame)
        {
            Show(latest.Frame, latest.Version);
        }
    }

    private void Show(GlyphLens.Core.Models.CapturedFrame frame, long version)
    {
        var metadata = frame.Metadata;
        if (_bitmap is null || _bitmap.PixelWidth != metadata.Width || _bitmap.PixelHeight != metadata.Height)
        {
            // Bgr32 ignores the alpha channel, so a frame whose alpha is not 255 can never render transparent.
            _bitmap = new WriteableBitmap(metadata.Width, metadata.Height, 96, 96, PixelFormats.Bgr32, null);
            Source = _bitmap;
        }

        if (!MemoryMarshal.TryGetArray(frame.Pixels, out var segment) || segment.Array is null)
        {
            return;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, metadata.Width, metadata.Height), segment.Array, metadata.Stride, segment.Offset);
        _shownVersion = version;
    }
}
