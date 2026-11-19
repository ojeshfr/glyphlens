using GlyphLens.Core.Models;

namespace GlyphLens.Core.Events;

/// <summary>
/// Raised when a capture service changes <see cref="CaptureStatus"/>.
/// </summary>
/// <remarks>May be raised on any thread; UI consumers must marshal to their own thread.</remarks>
public sealed class CaptureStatusChangedEventArgs : EventArgs
{
    public CaptureStatusChangedEventArgs(CaptureStatus previousStatus, CaptureStatus currentStatus, CaptureError? error = null)
    {
        PreviousStatus = previousStatus;
        CurrentStatus = currentStatus;
        Error = error;
    }

    public CaptureStatus PreviousStatus { get; }

    public CaptureStatus CurrentStatus { get; }

    /// <summary>Set when <see cref="CurrentStatus"/> is <see cref="CaptureStatus.Faulted"/>.</summary>
    public CaptureError? Error { get; }
}
