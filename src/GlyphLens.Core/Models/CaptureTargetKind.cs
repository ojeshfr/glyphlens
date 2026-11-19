namespace GlyphLens.Core.Models;

/// <summary>
/// The kind of thing that can be captured.
/// </summary>
public enum CaptureTargetKind
{
    /// <summary>An entire display (monitor).</summary>
    Display = 1,

    /// <summary>A single application window.</summary>
    Window = 2,
}
