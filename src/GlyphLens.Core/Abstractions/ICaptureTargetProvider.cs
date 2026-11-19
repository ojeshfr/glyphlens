using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Enumerates the displays and windows that can currently be captured.
/// </summary>
public interface ICaptureTargetProvider
{
    /// <summary>
    /// Returns a snapshot of the capturable targets: displays first, then windows. This performs
    /// operating-system enumeration and may take tens of milliseconds, so call it off the UI thread.
    /// </summary>
    IReadOnlyList<CaptureTarget> GetTargets();
}
