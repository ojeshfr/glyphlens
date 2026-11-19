using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Capture;

/// <summary>Builds small frames and tracks when their buffers are released.</summary>
internal sealed class TestFrames
{
    private int _released;

    /// <summary>Number of frames created through this factory whose buffer has been released.</summary>
    public int Released => Volatile.Read(ref _released);

    public int Created { get; private set; }

    /// <summary>Frames created but not yet released: the number of live buffers.</summary>
    public int Live => Created - Released;

    public CapturedFrame Create(long sequence = 1, int width = 4, int height = 2, string targetId = "display:test")
    {
        Created++;
        var metadata = new FrameMetadata(
            targetId,
            width,
            height,
            width * FrameMetadata.BytesPerPixel,
            PixelFormat.Bgra8,
            new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
            sequence);
        return new CapturedFrame(metadata, new byte[metadata.ByteLength + 16], _ => Interlocked.Increment(ref _released));
    }
}
