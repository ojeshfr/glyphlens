namespace GlyphLens.Core.Models;

/// <summary>
/// Describes something GlyphLens can capture: a display or an application window.
/// </summary>
/// <remarks>
/// A target is a snapshot taken at enumeration time. Windows can close and displays can be
/// disconnected afterwards, so starting a capture re-validates the target and may fail with
/// <see cref="CaptureErrorKind.TargetNotFound"/>. <see cref="Id"/> is opaque to consumers; it is
/// produced and interpreted by the platform capture implementation.
/// </remarks>
public sealed record CaptureTarget
{
    public CaptureTarget(
        string id,
        CaptureTargetKind kind,
        string displayName,
        int width,
        int height,
        bool isPrimary = false,
        string? processName = null,
        ScreenRectangle? bounds = null,
        DpiScale? dpi = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown capture target kind.");
        }

        if (isPrimary && kind != CaptureTargetKind.Display)
        {
            throw new ArgumentException("Only a display can be primary.", nameof(isPrimary));
        }

        Id = id;
        Kind = kind;
        DisplayName = displayName;
        Width = width;
        Height = height;
        IsPrimary = isPrimary;
        ProcessName = string.IsNullOrWhiteSpace(processName) ? null : processName;
        Bounds = bounds;
        Dpi = dpi;
    }

    /// <summary>Opaque, platform-specific identifier. Also used as <see cref="FrameMetadata.TargetId"/>.</summary>
    public string Id { get; }

    public CaptureTargetKind Kind { get; }

    /// <summary>Display name, or the window title.</summary>
    public string DisplayName { get; }

    /// <summary>Width in physical pixels.</summary>
    public int Width { get; }

    /// <summary>Height in physical pixels.</summary>
    public int Height { get; }

    /// <summary>For displays: whether this is the primary display. Always false for windows.</summary>
    public bool IsPrimary { get; }

    /// <summary>For windows: the owning process name, when it could be determined.</summary>
    public string? ProcessName { get; }

    /// <summary>
    /// Location in virtual-screen physical pixels at enumeration time (may have negative coordinates).
    /// Informational for windows, which can move after enumeration.
    /// </summary>
    public ScreenRectangle? Bounds { get; }

    /// <summary>For displays: the display's scale factor. Not known for windows.</summary>
    public DpiScale? Dpi { get; }
}
