namespace GlyphLens.Core.Models;

/// <summary>
/// Category of a capture failure.
/// </summary>
public enum CaptureErrorKind
{
    /// <summary>Screen capture is not available on this system (unsupported Windows version or policy).</summary>
    Unsupported = 1,

    /// <summary>The target no longer exists or could not be found.</summary>
    TargetNotFound = 2,

    /// <summary>The target went away during capture (window closed, display disconnected).</summary>
    TargetClosed = 3,

    /// <summary>Windows refused access to the target (for example a window of an elevated process).</summary>
    AccessDenied = 4,

    /// <summary>The graphics device failed or was removed (driver reset, GPU removed).</summary>
    DeviceLost = 5,

    /// <summary>Frames repeatedly failed to process.</summary>
    FrameProcessing = 6,

    /// <summary>Any failure that does not fit another category.</summary>
    Unexpected = 7,
}

/// <summary>
/// A capture failure with a message that is safe to show to the user.
/// </summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Message">A user-readable explanation. Never contains captured content.</param>
public sealed record CaptureError(CaptureErrorKind Kind, string Message)
{
    /// <summary>Technical detail for logs (exception type and HRESULT); not shown as the primary UI message.</summary>
    public string? Detail { get; init; }
}
