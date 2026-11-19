namespace GlyphLens.Core.Models;

/// <summary>
/// Lifecycle state of the local inference worker process (GlyphLens.Worker.exe).
/// </summary>
public enum WorkerStatus
{
    /// <summary>The worker process has not been started, or has exited cleanly.</summary>
    Stopped = 0,

    /// <summary>The worker process is launching and has not yet completed its handshake.</summary>
    Starting = 1,

    /// <summary>The worker completed its handshake and accepts requests.</summary>
    Ready = 2,

    /// <summary>The worker cannot be used (for example: not installed, or required models are missing).</summary>
    Unavailable = 3,

    /// <summary>The worker failed (crashed, timed out, or reported a fatal error).</summary>
    Error = 4,
}
