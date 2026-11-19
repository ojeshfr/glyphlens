using System.Runtime.InteropServices;
using GlyphLens.Core.Capture;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Capture;

namespace GlyphLens.Windows.Tests.Capture;

public class CaptureErrorMapperTests
{
    private static readonly CaptureTarget Window = new("window:1", CaptureTargetKind.Window, "w", 10, 10);
    private static readonly CaptureTarget Display = new("display:1", CaptureTargetKind.Display, "d", 10, 10);

    [Fact]
    public void CaptureException_PassesItsErrorThrough()
    {
        var error = new CaptureError(CaptureErrorKind.Unsupported, "nope");

        Assert.Same(error, CaptureErrorMapper.Map(new CaptureException(error)));
    }

    [Fact]
    public void AccessDenied_ExplainsElevatedWindows()
    {
        var error = CaptureErrorMapper.Map(new COMException("denied", CaptureErrorMapper.EAccessDenied), Window);

        Assert.Equal(CaptureErrorKind.AccessDenied, error.Kind);
        Assert.Contains("administrator", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnauthorizedAccessException_IsAccessDenied()
    {
        Assert.Equal(CaptureErrorKind.AccessDenied, CaptureErrorMapper.Map(new UnauthorizedAccessException(), Window).Kind);
    }

    [Theory]
    [InlineData(CaptureErrorMapper.DxgiErrorDeviceRemoved)]
    [InlineData(CaptureErrorMapper.DxgiErrorDeviceHung)]
    [InlineData(CaptureErrorMapper.DxgiErrorDeviceReset)]
    [InlineData(CaptureErrorMapper.DxgiErrorDriverInternalError)]
    [InlineData(CaptureErrorMapper.DxgiErrorAccessLost)]
    public void GraphicsDeviceFailures_AreDeviceLost(int hresult)
    {
        var error = CaptureErrorMapper.Map(new COMException("gpu", hresult), Display);

        Assert.Equal(CaptureErrorKind.DeviceLost, error.Kind);
        Assert.True(CaptureErrorMapper.IsDeviceLost(new COMException("gpu", hresult)));
    }

    [Fact]
    public void InvalidWindowHandle_IsTargetNotFound_WithWindowWording()
    {
        var error = CaptureErrorMapper.Map(new COMException("bad hwnd", CaptureErrorMapper.ErrorInvalidWindowHandle), Window);

        Assert.Equal(CaptureErrorKind.TargetNotFound, error.Kind);
        Assert.Contains("window", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidArgumentForADisplay_IsTargetNotFound_WithDisplayWording()
    {
        var error = CaptureErrorMapper.Map(new COMException("bad monitor", CaptureErrorMapper.EInvalidArg), Display);

        Assert.Equal(CaptureErrorKind.TargetNotFound, error.Kind);
        Assert.Contains("display", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingNativeEntryPoints_AreUnsupported()
    {
        Assert.Equal(CaptureErrorKind.Unsupported, CaptureErrorMapper.Map(new DllNotFoundException()).Kind);
        Assert.Equal(CaptureErrorKind.Unsupported, CaptureErrorMapper.Map(new EntryPointNotFoundException()).Kind);
    }

    [Fact]
    public void UnknownFailures_AreUnexpected_AndKeepTechnicalDetailSeparate()
    {
        var error = CaptureErrorMapper.Map(new InvalidOperationException("something odd"), Window);

        Assert.Equal(CaptureErrorKind.Unexpected, error.Kind);
        Assert.DoesNotContain("something odd", error.Message, StringComparison.Ordinal);
        Assert.Contains("something odd", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceLostError_ProvidesUserGuidance()
    {
        var error = CaptureErrorMapper.DeviceLostError("detail");

        Assert.Equal(CaptureErrorKind.DeviceLost, error.Kind);
        Assert.Equal("detail", error.Detail);
        Assert.Contains("again", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryExceptions_AreNotDeviceLost()
    {
        Assert.False(CaptureErrorMapper.IsDeviceLost(new InvalidOperationException()));
    }
}
