using GlyphLens.Core.Models;
using GlyphLens.Windows.Platform;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// Answers "does this capture target still exist?" for the session watchdog.
/// </summary>
/// <remarks>
/// <c>GraphicsCaptureItem.Closed</c> is not reliable on its own: Windows implements it with an out-of-context
/// window-event hook that is only delivered to a thread that pumps messages, and capture sessions are created on
/// thread-pool threads. (Measured: killing the captured window's process raised no <c>Closed</c> event in 6 s.)
/// The watchdog polls this check instead, and the event remains as a secondary, faster signal where it works.
/// </remarks>
internal static unsafe class TargetLiveness
{
    internal static bool IsAlive(CaptureTargetKind kind, nint handle) => kind switch
    {
        CaptureTargetKind.Window => NativeMethods.IsWindow(handle),
        CaptureTargetKind.Display => IsMonitorValid(handle),
        _ => true,
    };

    private static bool IsMonitorValid(nint monitor)
    {
        // A disconnected monitor's HMONITOR becomes invalid, so querying it fails.
        var info = new NativeMonitorInfoEx { Size = sizeof(NativeMonitorInfoEx) };
        return NativeMethods.GetMonitorInfo(monitor, ref info);
    }
}
