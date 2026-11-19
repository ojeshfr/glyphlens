using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Capture;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Platform;
using Windows.Graphics.Capture;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// <see cref="IScreenCaptureService"/> implemented with Windows Graphics Capture.
/// </summary>
/// <remarks>
/// <para>
/// Start and stop calls are serialised by a gate, so they never overlap. Each start builds a brand-new
/// <see cref="CaptureSessionManager"/>; each stop (or fault) disposes it, which releases the capture session, frame
/// pool and staging texture. The Direct3D device is shared by successive sessions and flushed around every
/// session (see <see cref="Direct3DDeviceManager.Flush"/>); it is destroyed with the service, or earlier if the
/// GPU reports it lost. Failures reported by the session (target closed, device lost)
/// tear the session down on a background task and move the service to <see cref="CaptureStatus.Faulted"/>; a
/// callback from a session that has since been replaced is ignored.
/// </para>
/// <para>Frames are published to <see cref="Frames"/> (latest-wins, in memory only).</para>
/// </remarks>
public sealed class WindowsGraphicsCaptureService : IScreenCaptureService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CaptureStateMachine _state = new();
    private readonly LatestFrameMailbox _mailbox = new();
    private readonly WindowsCaptureOptions _options;
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(300);

    private Direct3DDeviceManager? _device;
    private CaptureSessionManager? _session;
    private CaptureTarget? _currentTarget;
    private volatile bool _disposed;

    public WindowsGraphicsCaptureService()
        : this(new WindowsCaptureOptions())
    {
    }

    public WindowsGraphicsCaptureService(WindowsCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

    public CaptureStatus Status
    {
        get
        {
            lock (_state)
            {
                return _state.Status;
            }
        }
    }

    public CaptureTarget? CurrentTarget => Volatile.Read(ref _currentTarget);

    public IFrameSource Frames => _mailbox;

    public async Task StartAsync(CaptureTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Status == CaptureStatus.Capturing)
            {
                throw new InvalidOperationException("A capture is already running. Stop it before starting another.");
            }

            Transition(CaptureStatus.Starting, null);

            CaptureSessionManager? session = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Setup touches COM and the GPU; keep it off the caller's (possibly UI) thread.
                session = await Task.Run(() => CreateSession(target), CancellationToken.None).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);

                _session = session;
                Volatile.Write(ref _currentTarget, target);
                session = null; // ownership moved to the field
                Transition(CaptureStatus.Capturing, null);
            }
            catch (OperationCanceledException)
            {
                DisposeOffThread(session);
                Transition(CaptureStatus.Idle, null);
                throw;
            }
            catch (ObjectDisposedException)
            {
                DisposeOffThread(session);
                throw;
            }
            catch (Exception ex)
            {
                DisposeOffThread(session);
                var error = CaptureErrorMapper.Map(ex, target);
                if (error.Kind == CaptureErrorKind.DeviceLost)
                {
                    await DisposeDeviceAsync().ConfigureAwait(false);
                }

                Transition(CaptureStatus.Faulted, error);
                throw new CaptureException(error, ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true; // from here on, late session callbacks are ignored
            await StopCoreAsync().ConfigureAwait(false);
            await DisposeDeviceAsync().ConfigureAwait(false);
            _mailbox.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var status = Status;
        if (status is CaptureStatus.Idle)
        {
            return;
        }

        if (status == CaptureStatus.Faulted)
        {
            Transition(CaptureStatus.Idle, null); // resources were already released when the fault occurred
            return;
        }

        Transition(CaptureStatus.Stopping, null);
        await ReleaseSessionAsync().ConfigureAwait(false);
        Transition(CaptureStatus.Idle, null);
    }

    private async Task ReleaseSessionAsync()
    {
        var session = _session;
        _session = null;
        Volatile.Write(ref _currentTarget, null);

        if (session is not null)
        {
            // Dispose waits for an in-flight frame callback to finish; do that off the caller's thread.
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }

        _mailbox.Clear();
        ScheduleFlush();
    }

    /// <summary>Flushes the shared device now (releasing the previous session's deferred resources) and once more shortly after.</summary>
    private void ScheduleFlush()
    {
        var device = _device;
        if (device is null)
        {
            return;
        }

        device.Flush();

        // Windows Graphics Capture releases a pool's textures asynchronously after Close(), and the projected WinRT
        // objects keep per-interface COM references that only their finalizers drop. Wait for the asynchronous
        // release, finalize the projections, then flush so Direct3D destroys the textures (and their kernel Section
        // handles). Without this the process holds one handle and GPU buffer per pool buffer per stop until some
        // unrelated GC happens to run. Stopping is rare and user-initiated, so one background collection is cheap.
        _ = Task.Run(async () =>
        {
            await Task.Delay(FlushDelay).ConfigureAwait(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            device.Flush();
        });
    }

    private async Task DisposeDeviceAsync()
    {
        var device = _device;
        _device = null;
        if (device is not null)
        {
            device.Flush();
            await Task.Delay(FlushDelay).ConfigureAwait(false);
            await Task.Run(device.Dispose).ConfigureAwait(false);
        }
    }

    private Direct3DDeviceManager EnsureDevice()
    {
        _device ??= Direct3DDeviceManager.Create();
        _device.Flush(); // release anything the previous session left pending
        return _device;
    }

    private CaptureSessionManager CreateSession(CaptureTarget target)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new CaptureException(new CaptureError(
                CaptureErrorKind.Unsupported,
                "Windows Graphics Capture is not supported on this system. Windows 10 version 2004 or later is required."));
        }

        if (!CaptureTargetId.TryParse(target.Id, out var kind, out var handle) || kind != target.Kind)
        {
            throw new CaptureException(new CaptureError(
                CaptureErrorKind.TargetNotFound,
                "That capture target is not recognised. Refresh the list and choose another."));
        }

        if (kind == CaptureTargetKind.Window && !NativeMethods.IsWindow(handle))
        {
            throw new CaptureException(new CaptureError(
                CaptureErrorKind.TargetNotFound,
                "That window no longer exists. Refresh the list and choose another."));
        }

        var item = kind == CaptureTargetKind.Window
            ? WinRtInterop.CreateItemForWindow(handle)
            : WinRtInterop.CreateItemForMonitor(handle);

        var device = EnsureDevice();
        var generation = new SessionCallbacks(this);
        var session = CaptureSessionManager.Start(
            target,
            item,
            device,
            _options,
            () => TargetLiveness.IsAlive(kind, handle),
            generation.OnFrame,
            generation.OnFault);
        generation.Bind(session);
        return session;
    }

    private void OnSessionFault(CaptureSessionManager session, CaptureError error)
    {
        // Runs on a frame/event thread while the session holds its lock: never tear down here, hand off.
        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed || !ReferenceEquals(_session, session) || Status != CaptureStatus.Capturing)
                    {
                        return; // a stale callback from a session that has already been stopped or replaced
                    }

                    await ReleaseSessionAsync().ConfigureAwait(false);
                    if (error.Kind == CaptureErrorKind.DeviceLost)
                    {
                        await DisposeDeviceAsync().ConfigureAwait(false); // the GPU device is unusable; recreate on next start
                    }

                    Transition(CaptureStatus.Faulted, error);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (ObjectDisposedException)
            {
                // Service disposed while the fault was being handled; nothing left to report.
            }
        });
    }

    private static void DisposeOffThread(CaptureSessionManager? session)
    {
        if (session is not null)
        {
            _ = Task.Run(session.Dispose);
        }
    }

    private void Transition(CaptureStatus next, CaptureError? error)
    {
        CaptureStatus previous;
        lock (_state)
        {
            previous = _state.Status;
            _state.TransitionTo(next);
        }

        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

        var args = new CaptureStatusChangedEventArgs(previous, next, error);
        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<CaptureStatusChangedEventArgs>>())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A faulty subscriber must not corrupt capture state or stop other subscribers.
            }
        }
    }

    /// <summary>
    /// Connects one session's callbacks to this service, remembering which session they belong to so a callback
    /// from a replaced session can be recognised and ignored.
    /// </summary>
    private sealed class SessionCallbacks(WindowsGraphicsCaptureService owner)
    {
        private CaptureSessionManager? _session;

        internal void Bind(CaptureSessionManager session) => _session = session;

        internal void OnFrame(CapturedFrame frame) => owner._mailbox.Publish(frame);

        internal void OnFault(CaptureError error)
        {
            var session = _session;
            if (session is not null)
            {
                owner.OnSessionFault(session, error);
            }
        }
    }
}
