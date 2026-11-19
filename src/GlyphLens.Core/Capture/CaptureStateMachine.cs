using GlyphLens.Core.Models;

namespace GlyphLens.Core.Capture;

/// <summary>
/// Enforces the legal <see cref="CaptureStatus"/> transitions of a capture service.
/// </summary>
/// <remarks>
/// <code>
/// Idle ──► Starting ──► Capturing ──► Stopping ──► Idle
///            │  │            │
///            │  └──► Idle    └──► Faulted ──► Starting | Idle
///            └──► Faulted
/// </code>
/// Not thread-safe: the owner serialises access.
/// </remarks>
public sealed class CaptureStateMachine
{
    public CaptureStatus Status { get; private set; } = CaptureStatus.Idle;

    /// <summary>Returns whether a service may move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool IsValidTransition(CaptureStatus from, CaptureStatus to) => (from, to) switch
    {
        (CaptureStatus.Idle, CaptureStatus.Starting) => true,
        (CaptureStatus.Starting, CaptureStatus.Capturing) => true,
        (CaptureStatus.Starting, CaptureStatus.Faulted) => true,
        (CaptureStatus.Starting, CaptureStatus.Idle) => true, // cancelled during start
        (CaptureStatus.Capturing, CaptureStatus.Stopping) => true,
        (CaptureStatus.Capturing, CaptureStatus.Faulted) => true,
        (CaptureStatus.Stopping, CaptureStatus.Idle) => true,
        (CaptureStatus.Faulted, CaptureStatus.Starting) => true,
        (CaptureStatus.Faulted, CaptureStatus.Idle) => true, // fault acknowledged by a stop
        _ => false,
    };

    /// <summary>Moves to <paramref name="next"/> or throws when the transition is illegal.</summary>
    /// <exception cref="InvalidOperationException">The transition is not allowed from the current state.</exception>
    public CaptureStatus TransitionTo(CaptureStatus next)
    {
        if (!TryTransitionTo(next))
        {
            throw new InvalidOperationException($"Illegal capture status transition {Status} -> {next}.");
        }

        return Status;
    }

    /// <summary>Moves to <paramref name="next"/> if allowed; returns whether the transition happened.</summary>
    public bool TryTransitionTo(CaptureStatus next)
    {
        if (!IsValidTransition(Status, next))
        {
            return false;
        }

        Status = next;
        return true;
    }
}
