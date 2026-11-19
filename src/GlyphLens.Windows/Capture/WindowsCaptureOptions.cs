namespace GlyphLens.Windows.Capture;

/// <summary>
/// Tunable settings of <see cref="WindowsGraphicsCaptureService"/>.
/// </summary>
public sealed record WindowsCaptureOptions
{
    /// <summary>
    /// Upper bound on frames processed (read back to CPU memory and published) per second. Frames arriving
    /// faster are discarded without any GPU readback. Default 30, which is smooth for preview and ample for OCR.
    /// </summary>
    public int MaxFramesPerSecond { get; init; } = 30;

    /// <summary>Number of buffers in the Windows Graphics Capture frame pool (2 is the recommended minimum).</summary>
    public int FramePoolBufferCount { get; init; } = 2;

    /// <summary>Consecutive frame-processing failures tolerated before the capture is faulted.</summary>
    public int MaxConsecutiveFrameFailures { get; init; } = 5;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxFramesPerSecond, 240);
        ArgumentOutOfRangeException.ThrowIfLessThan(FramePoolBufferCount, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConsecutiveFrameFailures, 1);
    }
}
