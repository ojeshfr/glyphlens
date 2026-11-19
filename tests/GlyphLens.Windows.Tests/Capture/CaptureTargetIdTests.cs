using GlyphLens.Core.Models;
using GlyphLens.Windows.Capture;

namespace GlyphLens.Windows.Tests.Capture;

public class CaptureTargetIdTests
{
    [Theory]
    [InlineData(0x10001L)]
    [InlineData(0x7FFFFFF0L)]
    [InlineData(0x00000000DEADBEEFL)]
    [InlineData(0x1234567890ABCDL)]
    public void Display_RoundTrips(long handle)
    {
        var id = CaptureTargetId.ForDisplay((nint)handle);

        Assert.True(CaptureTargetId.TryParse(id, out var kind, out var parsed));
        Assert.Equal(CaptureTargetKind.Display, kind);
        Assert.Equal((nint)handle, parsed);
    }

    [Theory]
    [InlineData(0x10001L)]
    [InlineData(0x1234567890ABCDL)]
    public void Window_RoundTrips(long handle)
    {
        var id = CaptureTargetId.ForWindow((nint)handle);

        Assert.True(CaptureTargetId.TryParse(id, out var kind, out var parsed));
        Assert.Equal(CaptureTargetKind.Window, kind);
        Assert.Equal((nint)handle, parsed);
    }

    [Fact]
    public void Ids_AreFixedWidthLowercaseHexWithAKindPrefix()
    {
        Assert.Equal("display:0x000000000000abcd", CaptureTargetId.ForDisplay(0xABCD));
        Assert.Equal("window:0x0000000000000042", CaptureTargetId.ForWindow(0x42));
    }

    [Fact]
    public void DisplayAndWindowIdsForTheSameHandle_AreDifferent()
    {
        Assert.NotEqual(CaptureTargetId.ForDisplay(5), CaptureTargetId.ForWindow(5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("display:")]
    [InlineData("display:0x")]
    [InlineData("window:0x0")]
    [InlineData("display:0x0000000000000000")]
    [InlineData("screen:0x10")]
    [InlineData("0x10")]
    [InlineData("window:0xZZZZ")]
    [InlineData("window:0x-1")]
    [InlineData("window:0x00000000000000001")]
    [InlineData("WINDOW:0x10")]
    public void TryParse_RejectsMalformedIds(string? id)
    {
        Assert.False(CaptureTargetId.TryParse(id, out _, out var handle));
        Assert.Equal(0, handle);
    }
}
