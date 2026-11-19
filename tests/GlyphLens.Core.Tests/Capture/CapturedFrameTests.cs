using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Capture;

public class CapturedFrameTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Pixels_ExposesExactlyStrideTimesHeightBytesOfALargerBuffer()
    {
        var metadata = new FrameMetadata("t", 3, 2, 16, PixelFormat.Bgra8, SampleTime, 1);
        var buffer = new byte[100];
        buffer[0] = 7;

        using var frame = new CapturedFrame(metadata, buffer);

        Assert.Equal(32, frame.Pixels.Length);
        Assert.Equal(7, frame.Pixels.Span[0]);
    }

    [Fact]
    public void Constructor_RejectsBufferSmallerThanStrideTimesHeight()
    {
        var metadata = new FrameMetadata("t", 4, 2, 16, PixelFormat.Bgra8, SampleTime, 1);

        Assert.Throws<ArgumentException>(() => new CapturedFrame(metadata, new byte[31]));
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        var metadata = new FrameMetadata("t", 1, 1, 4, PixelFormat.Bgra8, SampleTime, 1);

        Assert.Throws<ArgumentNullException>(() => new CapturedFrame(null!, new byte[4]));
        Assert.Throws<ArgumentNullException>(() => new CapturedFrame(metadata, null!));
    }

    [Fact]
    public void Dispose_ReleasesBufferExactlyOnce()
    {
        var frames = new TestFrames();
        var frame = frames.Create();

        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, frames.Released);
        Assert.Equal(0, frame.ReferenceCount);
    }

    [Fact]
    public void Retain_KeepsBufferAliveUntilEveryReferenceIsReleased()
    {
        var frames = new TestFrames();
        var frame = frames.Create();

        var consumer = frame.Retain();
        Assert.Same(frame, consumer);
        Assert.Equal(2, frame.ReferenceCount);

        frame.Dispose(); // the producer lets go first
        Assert.Equal(0, frames.Released);
        Assert.Equal(32, consumer.Pixels.Length); // still readable by the consumer (4 px * 4 bytes * 2 rows)

        consumer.Dispose();
        Assert.Equal(1, frames.Released);
    }

    [Fact]
    public void AfterRelease_PixelsAndRetainThrowObjectDisposed()
    {
        var frames = new TestFrames();
        var frame = frames.Create();
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
    }

    [Fact]
    public void MetadataRemainsReadableAfterRelease()
    {
        var frames = new TestFrames();
        var frame = frames.Create(sequence: 42);
        frame.Dispose();

        Assert.Equal(42, frame.Metadata.Sequence);
    }

    [Fact]
    public async Task ConcurrentRetainAndDispose_ReleaseTheBufferExactlyOnce()
    {
        var frames = new TestFrames();
        var frame = frames.Create();

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                using var held = frame.Retain();
                _ = held.Pixels.Length;
            }
        }));
        await Task.WhenAll(workers);

        Assert.Equal(1, frame.ReferenceCount);
        Assert.Equal(0, frames.Released);
        frame.Dispose();
        Assert.Equal(1, frames.Released);
    }
}
