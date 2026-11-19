using GlyphLens.Core.Capture;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public class CaptureTargetTests
{
    [Fact]
    public void Display_SetsAllProperties()
    {
        var bounds = new ScreenRectangle(-1440, 200, 1440, 2560); // portrait monitor left of the primary
        var target = new CaptureTarget("display:1", CaptureTargetKind.Display, "Display 2", 1440, 2560, false, null, bounds, new DpiScale(1.25));

        Assert.Equal("display:1", target.Id);
        Assert.Equal(CaptureTargetKind.Display, target.Kind);
        Assert.Equal("Display 2", target.DisplayName);
        Assert.Equal(1440, target.Width);
        Assert.Equal(2560, target.Height);
        Assert.False(target.IsPrimary);
        Assert.Equal(bounds, target.Bounds);
        Assert.Equal(125, target.Dpi?.Percent);
    }

    [Fact]
    public void Window_CarriesProcessName_AndNormalisesBlankToNull()
    {
        var named = new CaptureTarget("window:1", CaptureTargetKind.Window, "Inbox - Mail", 1280, 720, processName: "chrome");
        var blank = new CaptureTarget("window:2", CaptureTargetKind.Window, "Untitled", 100, 100, processName: "  ");

        Assert.Equal("chrome", named.ProcessName);
        Assert.Null(blank.ProcessName);
    }

    [Fact]
    public void PortraitDisplays_AreSupported()
    {
        var target = new CaptureTarget("display:p", CaptureTargetKind.Display, "Display 3", 1080, 1920);

        Assert.True(target.Height > target.Width);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_RejectsBlankId(string id)
    {
        Assert.Throws<ArgumentException>(() => new CaptureTarget(id, CaptureTargetKind.Display, "n", 1, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsBlankName(string name)
    {
        Assert.Throws<ArgumentException>(() => new CaptureTarget("id", CaptureTargetKind.Display, name, 1, 1));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public void Constructor_RejectsNonPositiveSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureTarget("id", CaptureTargetKind.Display, "n", width, height));
    }

    [Fact]
    public void Constructor_RejectsUndefinedKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureTarget("id", (CaptureTargetKind)42, "n", 1, 1));
    }

    [Fact]
    public void OnlyDisplaysCanBePrimary()
    {
        Assert.Throws<ArgumentException>(() => new CaptureTarget("id", CaptureTargetKind.Window, "n", 1, 1, isPrimary: true));
    }

    [Fact]
    public void Targets_WithSameValues_AreEqual()
    {
        var a = new CaptureTarget("id", CaptureTargetKind.Display, "n", 1, 1, true);
        var b = new CaptureTarget("id", CaptureTargetKind.Display, "n", 1, 1, true);

        Assert.Equal(a, b);
    }
}

public class CaptureErrorTests
{
    [Fact]
    public void CaptureException_CarriesItsError()
    {
        var error = new CaptureError(CaptureErrorKind.AccessDenied, "No access.") { Detail = "0x80070005" };

        var ex = new CaptureException(error);

        Assert.Same(error, ex.Error);
        Assert.Equal("No access.", ex.Message);
    }

    [Fact]
    public void CaptureException_KeepsTheInnerException()
    {
        var inner = new InvalidOperationException("inner");

        var ex = new CaptureException(new CaptureError(CaptureErrorKind.Unexpected, "outer"), inner);

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void CaptureException_StringConstructorProducesAnUnexpectedError()
    {
        Assert.Equal(CaptureErrorKind.Unexpected, new CaptureException("boom").Error.Kind);
    }

    [Fact]
    public void CaptureException_RejectsNullError()
    {
        Assert.Throws<ArgumentNullException>(() => new CaptureException((CaptureError)null!));
    }

    [Fact]
    public void StatusChangedEventArgs_CarryTransitionAndError()
    {
        var error = new CaptureError(CaptureErrorKind.TargetClosed, "closed");

        var args = new CaptureStatusChangedEventArgs(CaptureStatus.Capturing, CaptureStatus.Faulted, error);

        Assert.Equal(CaptureStatus.Capturing, args.PreviousStatus);
        Assert.Equal(CaptureStatus.Faulted, args.CurrentStatus);
        Assert.Same(error, args.Error);
    }
}
