using GlyphLens.Core.Models;

namespace GlyphLens.Core.Events;

/// <summary>
/// Raised when the local inference worker changes lifecycle state.
/// </summary>
public sealed class WorkerStatusChangedEventArgs : EventArgs
{
    public WorkerStatusChangedEventArgs(WorkerStatus previousStatus, WorkerStatus currentStatus, string? reason = null)
    {
        PreviousStatus = previousStatus;
        CurrentStatus = currentStatus;
        Reason = reason;
    }

    public WorkerStatus PreviousStatus { get; }

    public WorkerStatus CurrentStatus { get; }

    /// <summary>Optional human-readable reason, e.g. an error description. Must not contain screen text.</summary>
    public string? Reason { get; }
}
