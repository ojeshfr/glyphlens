using GlyphLens.Windows.Capture;

namespace GlyphLens.Windows.Tests.Capture;

public class FrameProcessorCopyRowsTests
{
    [Fact]
    public void PaddedRows_AreCompactedWithoutPaddingBytes()
    {
        // 2 pixels (8 bytes) per row, rows 12 bytes apart (4 bytes of GPU padding), 3 rows.
        byte[] source =
        [
            1, 2, 3, 4, 5, 6, 7, 8, 0xEE, 0xEE, 0xEE, 0xEE,
            11, 12, 13, 14, 15, 16, 17, 18, 0xEE, 0xEE, 0xEE, 0xEE,
            21, 22, 23, 24, 25, 26, 27, 28, 0xEE, 0xEE, 0xEE, 0xEE,
        ];
        var destination = new byte[24];

        FrameProcessor.CopyRows(source, 12, destination, 8, 3);

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, 17, 18, 21, 22, 23, 24, 25, 26, 27, 28],
            destination);
    }

    [Fact]
    public void TightRows_AreCopiedVerbatim()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8];
        var destination = new byte[8];

        FrameProcessor.CopyRows(source, 4, destination, 4, 2);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void LastRow_NeedsNoTrailingPadding()
    {
        // The mapped GPU buffer may end right after the last row's pixels.
        byte[] source = [1, 2, 3, 4, 0xEE, 0xEE, 5, 6, 7, 8];
        var destination = new byte[8];

        FrameProcessor.CopyRows(source, 6, destination, 4, 2);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], destination);
    }

    [Fact]
    public void OnlyTheVisibleWidthIsCopied_WhenTheSurfaceIsWiderThanTheContent()
    {
        byte[] source = [1, 2, 3, 4, 9, 9, 9, 9, 5, 6, 7, 8, 9, 9, 9, 9];
        var destination = new byte[8];

        FrameProcessor.CopyRows(source, 8, destination, 4, 2);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], destination);
    }

    [Fact]
    public void DestinationBeyondThePackedLength_IsLeftUntouched()
    {
        var destination = new byte[10];
        Array.Fill<byte>(destination, 0xAB);

        FrameProcessor.CopyRows([1, 2, 3, 4], 4, destination, 4, 1);

        Assert.Equal([1, 2, 3, 4, 0xAB, 0xAB, 0xAB, 0xAB, 0xAB, 0xAB], destination);
    }

    [Fact]
    public void ZeroRows_CopyNothing()
    {
        var destination = new byte[4];

        FrameProcessor.CopyRows([], 4, destination, 4, 0);

        Assert.All(destination, b => Assert.Equal(0, b));
    }

    [Fact]
    public void SourceShorterThanTheDescribedRows_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => FrameProcessor.CopyRows(new byte[11], 8, new byte[8], 4, 2));
    }

    [Fact]
    public void DestinationShorterThanThePackedRows_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => FrameProcessor.CopyRows(new byte[16], 8, new byte[7], 4, 2));
    }

    [Fact]
    public void PitchSmallerThanTheRow_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameProcessor.CopyRows(new byte[16], 3, new byte[8], 4, 2));
    }
}
