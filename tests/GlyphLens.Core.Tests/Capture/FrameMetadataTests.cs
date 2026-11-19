using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Capture;

public class FrameMetadataTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var bounds = new ScreenRectangle(-1920, 0, 1920, 1080);

        var metadata = new FrameMetadata("display:1", 1920, 1080, 7680, PixelFormat.Bgra8, SampleTime, 12, bounds);

        Assert.Equal("display:1", metadata.TargetId);
        Assert.Equal(1920, metadata.Width);
        Assert.Equal(1080, metadata.Height);
        Assert.Equal(7680, metadata.Stride);
        Assert.Equal(PixelFormat.Bgra8, metadata.PixelFormat);
        Assert.Equal(SampleTime, metadata.Timestamp);
        Assert.Equal(12, metadata.Sequence);
        Assert.Equal(bounds, metadata.ScreenBounds);
        Assert.Equal(7680 * 1080, metadata.ByteLength);
    }

    [Fact]
    public void ScreenBounds_IsOptional()
    {
        Assert.Null(new FrameMetadata("window:1", 10, 10, 40, PixelFormat.Bgra8, SampleTime, 1).ScreenBounds);
    }

    [Fact]
    public void Stride_MayExceedRowSizeForPaddedRows()
    {
        Assert.Equal(64, new FrameMetadata("t", 10, 2, 64, PixelFormat.Bgra8, SampleTime, 1).Stride);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_RejectsBlankTargetId(string targetId)
    {
        Assert.Throws<ArgumentException>(() => new FrameMetadata(targetId, 1, 1, 4, PixelFormat.Bgra8, SampleTime, 1));
    }

    [Fact]
    public void Constructor_RejectsNullTargetId()
    {
        Assert.Throws<ArgumentNullException>(() => new FrameMetadata(null!, 1, 1, 4, PixelFormat.Bgra8, SampleTime, 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-3, 1)]
    [InlineData(1, -3)]
    public void Constructor_RejectsNonPositiveDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameMetadata("t", width, height, 4096, PixelFormat.Bgra8, SampleTime, 1));
    }

    [Fact]
    public void Constructor_RejectsStrideSmallerThanOneRow()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new FrameMetadata("t", 4, 1, 15, PixelFormat.Bgra8, SampleTime, 1));
        Assert.Equal("stride", ex.ParamName);
    }

    [Fact]
    public void Constructor_RejectsNegativeSequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameMetadata("t", 1, 1, 4, PixelFormat.Bgra8, SampleTime, -1));
    }

    [Fact]
    public void Constructor_RejectsUndefinedPixelFormat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameMetadata("t", 1, 1, 4, (PixelFormat)99, SampleTime, 1));
    }

    [Fact]
    public void Constructor_RejectsFramesLargerThanTwoGibibytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameMetadata("t", 40_000, 40_000, 160_000, PixelFormat.Bgra8, SampleTime, 1));
    }

    [Fact]
    public void Records_WithSameValues_AreEqual()
    {
        var a = new FrameMetadata("t", 1, 1, 4, PixelFormat.Bgra8, SampleTime, 1);
        var b = new FrameMetadata("t", 1, 1, 4, PixelFormat.Bgra8, SampleTime, 1);

        Assert.Equal(a, b);
        Assert.NotEqual(a, new FrameMetadata("t", 1, 1, 4, PixelFormat.Bgra8, SampleTime, 2));
    }
}
