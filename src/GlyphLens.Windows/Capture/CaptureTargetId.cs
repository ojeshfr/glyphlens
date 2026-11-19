using System.Globalization;
using GlyphLens.Core.Models;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// Encodes native handles into the opaque <see cref="CaptureTarget.Id"/> strings used by Core.
/// </summary>
/// <remarks>
/// Format: <c>display:0x&lt;HMONITOR&gt;</c> or <c>window:0x&lt;HWND&gt;</c> (16 lowercase hex digits).
/// Handles can be reused by Windows after the original object is gone, so an id only identifies a target
/// for as long as that target exists; starting a capture re-validates it.
/// </remarks>
internal static class CaptureTargetId
{
    private const string DisplayPrefix = "display:0x";
    private const string WindowPrefix = "window:0x";

    internal static string ForDisplay(nint monitorHandle) => DisplayPrefix + Format(monitorHandle);

    internal static string ForWindow(nint windowHandle) => WindowPrefix + Format(windowHandle);

    internal static bool TryParse(string? id, out CaptureTargetKind kind, out nint handle)
    {
        kind = default;
        handle = 0;

        if (id is null)
        {
            return false;
        }

        string digits;
        if (id.StartsWith(DisplayPrefix, StringComparison.Ordinal))
        {
            kind = CaptureTargetKind.Display;
            digits = id[DisplayPrefix.Length..];
        }
        else if (id.StartsWith(WindowPrefix, StringComparison.Ordinal))
        {
            kind = CaptureTargetKind.Window;
            digits = id[WindowPrefix.Length..];
        }
        else
        {
            return false;
        }

        if (digits.Length is 0 or > 16 || !long.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value == 0)
        {
            return false;
        }

        handle = (nint)value;
        return true;
    }

    private static string Format(nint handle) => ((long)handle).ToString("x16", CultureInfo.InvariantCulture);
}
