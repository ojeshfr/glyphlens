namespace GlyphLens.Core.Models;

/// <summary>
/// Lifecycle state of a screen capture service.
/// </summary>
/// <remarks>
/// Valid transitions are defined by <see cref="Capture.CaptureStateMachine"/>.
/// </remarks>
public enum CaptureStatus
{
    /// <summary>Not capturing and not faulted. A capture can be started.</summary>
    Idle = 0,

    /// <summary>A capture session is being set up.</summary>
    Starting = 1,

    /// <summary>Frames are being captured and published.</summary>
    Capturing = 2,

    /// <summary>The capture session is being torn down.</summary>
    Stopping = 3,

    /// <summary>
    /// The capture ended because of an error (for example the target window closed).
    /// All native resources are already released; a new capture can be started.
    /// </summary>
    Faulted = 4,
}
