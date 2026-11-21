using GlyphLens.App.Overlay;

namespace GlyphLens.App.Tests;

public sealed class OverlayPlacementTests
{
    private static readonly PixelRect Primary = new(0, 0, 2560, 1560); // work area of a 2560x1600 display with a taskbar
    private static readonly (int Width, int Height) Min = (320, 120);

    private static PixelRect Fit(PixelRect desired, params PixelRect[] areas) =>
        OverlayPlacement.Fit(desired, areas.Length == 0 ? [Primary] : areas, areas.Length == 0 ? Primary : areas[0], Min, int.MaxValue);

    [Fact]
    public void AWindowInsideAMonitor_IsLeftWhereItIs()
    {
        var desired = new PixelRect(1000, 200, 600, 840);

        Assert.Equal(desired, Fit(desired));
    }

    [Fact]
    public void AWindowPartlyOffTheScreen_IsMovedBackInside()
    {
        var fitted = Fit(new PixelRect(2400, 1400, 600, 840));

        Assert.Equal(new PixelRect(1960, 720, 600, 840), fitted);
    }

    [Fact]
    public void AWindowAboveTheTopOrLeftOfTheDesktop_IsMovedBackInside()
    {
        Assert.Equal(new PixelRect(0, 0, 600, 840), Fit(new PixelRect(-300, -50, 600, 840)));
    }

    [Fact]
    public void ASavedPositionOnADisconnectedMonitor_MovesToThePrimaryMonitorsTopRight()
    {
        // The overlay was on a second monitor to the right (x from 2560); it is no longer connected.
        var fitted = Fit(new PixelRect(3200, 300, 600, 840));

        Assert.True(fitted.X >= Primary.X && fitted.Right <= Primary.Right);
        Assert.True(fitted.Y >= Primary.Y && fitted.Bottom <= Primary.Bottom);
        Assert.Equal(Primary.Right - 600 - OverlayPlacement.Margin, fitted.X);
        Assert.Equal(OverlayPlacement.Margin * 3, fitted.Y);
    }

    [Fact]
    public void ASecondMonitorWithNegativeCoordinates_IsAValidPlace()
    {
        var left = new PixelRect(-1920, 0, 1920, 1040);
        var desired = new PixelRect(-1500, 100, 400, 560);

        Assert.Equal(desired, OverlayPlacement.Fit(desired, [Primary, left], Primary, Min, int.MaxValue));
    }

    [Fact]
    public void TheMonitorThatHoldsMostOfTheWindowDecides()
    {
        var left = new PixelRect(-1920, 0, 1920, 1040);

        var fitted = OverlayPlacement.Fit(new PixelRect(-500, 100, 800, 560), [Primary, left], Primary, Min, int.MaxValue);

        // 500 of the 800 pixels are on the left monitor, so the window is pulled fully onto that one.
        Assert.Equal(-800, fitted.X);
        Assert.True(fitted.Right <= 0);
    }

    [Fact]
    public void AWindowLargerThanTheScreen_IsShrunkToFit()
    {
        var small = new PixelRect(0, 0, 1280, 680);

        var fitted = OverlayPlacement.Fit(new PixelRect(0, 0, 2000, 1500), [small], small, Min, int.MaxValue);

        Assert.Equal(small, fitted);
    }

    [Fact]
    public void TheMinimumSizeIsEnforced()
    {
        var fitted = Fit(new PixelRect(100, 100, 50, 10));

        Assert.Equal((320, 120), (fitted.Width, fitted.Height));
    }

    [Fact]
    public void TheMaximumWidthIsAConfigurableLimit()
    {
        var fitted = OverlayPlacement.Fit(new PixelRect(100, 100, 1500, 700), [Primary], Primary, Min, maxWidth: 900);

        Assert.Equal(900, fitted.Width);
    }

    [Fact]
    public void WithNoMonitorListTheOverlayStillGetsAPlace()
    {
        var fitted = OverlayPlacement.Fit(new PixelRect(5000, 5000, 400, 560), [], Primary, Min, int.MaxValue);

        Assert.True(fitted.X >= Primary.X && fitted.Right <= Primary.Right);
    }

    [Fact]
    public void AWindowWithOnlyItsBodyOnAMonitor_IsPulledBackSoTheHeaderIsReachable()
    {
        // Only the bottom 20 px of the window are on the screen: the header would be out of reach.
        var fitted = Fit(new PixelRect(1000, -820, 600, 840));

        Assert.True(fitted.Y >= 0);
    }

    [Fact]
    public void TheDefaultPlace_IsTheTopRightOfThePrimaryMonitor()
    {
        var place = OverlayPlacement.Default(Primary, (400, 560));

        Assert.Equal(new PixelRect(Primary.Right - 400 - OverlayPlacement.Margin, OverlayPlacement.Margin * 3, 400, 560), place);
    }

    [Fact]
    public void AMonitorWithTheTaskbarOnTheLeft_UsesItsWorkArea()
    {
        var work = new PixelRect(96, 0, 1824, 1080);

        var fitted = OverlayPlacement.Fit(new PixelRect(0, 100, 400, 560), [work], work, Min, int.MaxValue);

        Assert.Equal(96, fitted.X);
    }

    [Fact]
    public void PixelRect_ReportsOverlap()
    {
        var a = new PixelRect(0, 0, 100, 100);

        Assert.Equal((50, 50), a.OverlapWith(new PixelRect(50, 50, 100, 100)));
        Assert.Equal((0, 0), a.OverlapWith(new PixelRect(200, 200, 10, 10)));
        Assert.Equal(2500, a.OverlapArea(new PixelRect(50, 50, 100, 100)));
    }
}
