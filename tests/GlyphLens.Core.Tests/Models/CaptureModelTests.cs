using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public class CaptureModelTests
{
    [Fact]
    public void ScreenRectangle_AllowsNegativeOrigin()
    {
        var rect = new ScreenRectangle(-2560, -100, 800, 600);

        Assert.Equal(-2560, rect.X);
        Assert.Equal(-100, rect.Y);
        Assert.Equal(800, rect.Width);
        Assert.Equal(600, rect.Height);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void ScreenRectangle_RejectsNonPositiveSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenRectangle(0, 0, width, height));
    }
}
