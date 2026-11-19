using GlyphLens.Core.Capture;
using GlyphLens.Core.Models;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// Translates Windows exceptions and HRESULTs into <see cref="CaptureError"/>s with actionable messages.
/// </summary>
internal static class CaptureErrorMapper
{
    internal const int EAccessDenied = unchecked((int)0x80070005);
    internal const int EInvalidArg = unchecked((int)0x80070057);
    internal const int ErrorInvalidWindowHandle = unchecked((int)0x80070578);
    internal const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    internal const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    internal const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    internal const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);
    internal const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    internal static bool IsDeviceLostHResult(int hresult) =>
        hresult is DxgiErrorDeviceRemoved or DxgiErrorDeviceHung or DxgiErrorDeviceReset or DxgiErrorDriverInternalError;

    internal static CaptureError DeviceLostError(string? detail = null) =>
        new(
            CaptureErrorKind.DeviceLost,
            "The graphics device was reset or removed (for example by a driver update or display change). "
            + "Start the capture again.")
        {
            Detail = detail,
        };

    internal static CaptureError Map(Exception exception, CaptureTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is CaptureException captureException)
        {
            return captureException.Error;
        }

        var detail = $"{exception.GetType().Name} 0x{exception.HResult:X8}: {exception.Message}";
        var kind = target?.Kind;

        CaptureError Make(CaptureErrorKind errorKind, string message) =>
            new(errorKind, message) { Detail = detail };

        if (exception is UnauthorizedAccessException || exception.HResult == EAccessDenied)
        {
            return Make(
                CaptureErrorKind.AccessDenied,
                "Windows denied access to this capture target. Windows of apps running as administrator cannot "
                + "be captured by GlyphLens unless GlyphLens is also elevated.");
        }

        if (IsDeviceLostHResult(exception.HResult) || exception.HResult == DxgiErrorAccessLost)
        {
            return DeviceLostError(detail);
        }

        if (exception.HResult == ErrorInvalidWindowHandle
            || (exception.HResult == EInvalidArg && kind is CaptureTargetKind.Window or CaptureTargetKind.Display))
        {
            return Make(
                CaptureErrorKind.TargetNotFound,
                kind == CaptureTargetKind.Display
                    ? "That display is no longer available. Refresh the list and choose another."
                    : "That window no longer exists. Refresh the list and choose another.");
        }

        if (exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Make(
                CaptureErrorKind.Unsupported,
                "Windows Graphics Capture is not available on this system. Windows 10 version 2004 or later is required.");
        }

        return Make(CaptureErrorKind.Unexpected, "Capture failed unexpectedly. See details for more information.");
    }

    /// <summary>Returns true when <paramref name="exception"/> indicates the Direct3D device is gone.</summary>
    internal static bool IsDeviceLost(Exception exception) =>
        IsDeviceLostHResult(exception.HResult) || exception.HResult == DxgiErrorAccessLost;
}
