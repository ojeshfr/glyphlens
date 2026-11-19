using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Captures a display or window continuously and publishes frames through <see cref="Frames"/>.
/// </summary>
/// <remarks>
/// <para>Implementation: Windows Graphics Capture (<c>GlyphLens.Windows</c>).</para>
/// <para>
/// Only one capture session runs at a time. Frames stay in memory and are never persisted. Disposing
/// the service stops any running capture and releases all native resources.
/// </para>
/// </remarks>
public interface IScreenCaptureService : IAsyncDisposable
{
    CaptureStatus Status { get; }

    /// <summary>The target being captured, or <see langword="null"/> when not capturing.</summary>
    CaptureTarget? CurrentTarget { get; }

    /// <summary>Latest-frame publication point for consumers such as the preview and, later, OCR.</summary>
    IFrameSource Frames { get; }

    /// <summary>Raised after every status change, on an arbitrary thread.</summary>
    event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

    /// <summary>Starts capturing <paramref name="target"/>. Completes once capture is running.</summary>
    /// <exception cref="Capture.CaptureException">The capture could not be started; the status is then <see cref="CaptureStatus.Faulted"/>.</exception>
    /// <exception cref="InvalidOperationException">A capture is already running.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before capture began; the status returns to <see cref="CaptureStatus.Idle"/>.</exception>
    Task StartAsync(CaptureTarget target, CancellationToken cancellationToken = default);

    /// <summary>Stops capturing and releases the session's native resources. Safe to call when not capturing.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
