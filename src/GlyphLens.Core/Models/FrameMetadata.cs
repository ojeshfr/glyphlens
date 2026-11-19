namespace GlyphLens.Core.Models;

/// <summary>
/// Describes a <see cref="CapturedFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// Coordinate system: a frame covers the captured target in <b>physical pixels</b>, with the origin
/// at the top-left of the target and Y growing downward. Frame pixel (0,0) is the target's top-left;
/// frame pixels are never scaled by the display DPI. For a display capture, <see cref="ScreenBounds"/>
/// gives the display's location in virtual-screen pixels (which may be negative on multi-monitor
/// layouts), so frame pixel (x, y) corresponds to virtual-screen pixel
/// (<c>ScreenBounds.X + x</c>, <c>ScreenBounds.Y + y</c>).
/// </para>
/// </remarks>
public sealed record FrameMetadata
{
    /// <summary>Bytes per pixel of <see cref="PixelFormat.Bgra8"/>.</summary>
    public const int BytesPerPixel = 4;

    public FrameMetadata(
        string targetId,
        int width,
        int height,
        int stride,
        PixelFormat pixelFormat,
        DateTimeOffset timestamp,
        long sequence,
        ScreenRectangle? screenBounds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (!Enum.IsDefined(pixelFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, "Unknown pixel format.");
        }

        if (stride < (long)width * BytesPerPixel)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "Stride is smaller than one row of pixels.");
        }

        if ((long)stride * height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Frame is too large.");
        }

        TargetId = targetId;
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        Timestamp = timestamp;
        Sequence = sequence;
        ScreenBounds = screenBounds;
    }

    /// <summary>The <see cref="CaptureTarget.Id"/> this frame was captured from.</summary>
    public string TargetId { get; }

    /// <summary>Width in physical pixels.</summary>
    public int Width { get; }

    /// <summary>Height in physical pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row (at least <c>Width * 4</c>).</summary>
    public int Stride { get; }

    public PixelFormat PixelFormat { get; }

    /// <summary>When the frame was processed (wall clock).</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// 1-based sequence number within the current capture session; restarts at 1 for each session.
    /// Gaps mean frames were dropped (for example by frame-rate limiting).
    /// </summary>
    public long Sequence { get; }

    /// <summary>For display captures: the display's bounds in virtual-screen physical pixels. Null for windows.</summary>
    public ScreenRectangle? ScreenBounds { get; }

    /// <summary>Number of bytes occupied by the pixel data (<c>Stride * Height</c>).</summary>
    public int ByteLength => Stride * Height;
}
