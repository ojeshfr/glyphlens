using System.Diagnostics;
using GlyphLens.Core.Models;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// Owns every native resource of a single capture session and the callbacks that feed frames out of it.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: the session manager creates the frame processor, frame pool and capture session around a borrowed
/// <see cref="Direct3DDeviceManager"/> and releases all of them in <see cref="Dispose"/>. The device is owned by the
/// service and outlives the session (see <see cref="Direct3DDeviceManager.Flush"/>). A session is never reused:
/// every start creates a new one, so a stop/start cycle cannot leak state between runs.
/// </para>
/// <para>
/// Threading: the frame pool is free-threaded, so <c>FrameArrived</c> runs on thread-pool threads. All callback
/// work happens under <see cref="_gate"/>. <see cref="Dispose"/> takes the same lock before tearing anything down
/// and sets <see cref="_disposed"/>, so a callback either completes before disposal begins or observes the
/// disposed flag and returns without touching native objects. After <see cref="Dispose"/> returns, no
/// further callback is invoked.
/// </para>
/// <para>
/// The <c>onFrame</c> and <c>onFault</c> delegates are invoked while the lock is held and must return quickly
/// without calling back into this object.
/// </para>
/// </remarks>
internal sealed class CaptureSessionManager : IDisposable
{
    private const DirectXPixelFormat PoolPixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Direct3DDeviceManager _device;
    private readonly FrameProcessor _processor;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly CaptureTarget _target;
    private readonly WindowsCaptureOptions _options;
    private readonly Action<CapturedFrame> _onFrame;
    private readonly Action<CaptureError> _onFault;
    private readonly long _minFrameIntervalTicks;
    private readonly Func<bool> _isTargetAlive;
    private readonly Timer _watchdog;

    private SizeInt32 _poolSize;
    private long _arrivedFrames;
    private long _lastProcessedTimestamp;
    private int _consecutiveFailures;
    private bool _disposed;
    private bool _faultReported;

    private CaptureSessionManager(
        CaptureTarget target,
        WindowsCaptureOptions options,
        Direct3DDeviceManager device,
        FrameProcessor processor,
        GraphicsCaptureItem item,
        Func<bool> isTargetAlive,
        Action<CapturedFrame> onFrame,
        Action<CaptureError> onFault)
    {
        _target = target;
        _isTargetAlive = isTargetAlive;
        _options = options;
        _device = device;
        _processor = processor;
        _item = item;
        _onFrame = onFrame;
        _onFault = onFault;
        _minFrameIntervalTicks = Stopwatch.Frequency / options.MaxFramesPerSecond;

        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.WinRtDevice, PoolPixelFormat, options.FramePoolBufferCount, _poolSize);
        _session = _pool.CreateCaptureSession(item);
        TryDisableCursorCapture(_session);

        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;
        _watchdog = new Timer(static state => ((CaptureSessionManager)state!).CheckTargetAlive(), this, WatchdogInterval, WatchdogInterval);
    }

    /// <summary>The target this session is capturing.</summary>
    internal CaptureTarget Target => _target;

    /// <summary>
    /// Builds a session for <paramref name="item"/> and starts it. On any failure everything created so far is released.
    /// </summary>
    internal static CaptureSessionManager Start(
        CaptureTarget target,
        GraphicsCaptureItem item,
        Direct3DDeviceManager device,
        WindowsCaptureOptions options,
        Func<bool> isTargetAlive,
        Action<CapturedFrame> onFrame,
        Action<CaptureError> onFault)
    {
        var processor = new FrameProcessor(device);
        CaptureSessionManager? session = null;
        try
        {
            session = new CaptureSessionManager(target, options, device, processor, item, isTargetAlive, onFrame, onFault);
            session._session.StartCapture();
            return session;
        }
        catch
        {
            if (session is not null)
            {
                session.Dispose();
            }
            else
            {
                processor.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Stop callbacks first, then release in reverse order of creation. The device is borrowed, not owned.
            _watchdog.Dispose();
            _pool.FrameArrived -= OnFrameArrived;
            _item.Closed -= OnItemClosed;

            _session.Dispose();
            _pool.Dispose();
            _processor.Dispose();
        }
    }

    private static void TryDisableCursorCapture(GraphicsCaptureSession session)
    {
        try
        {
            session.IsCursorCaptureEnabled = false; // the pointer is irrelevant to text recognition
        }
        catch (Exception ex) when (ex is InvalidCastException or System.Runtime.InteropServices.COMException or NotImplementedException)
        {
            // Older builds do not expose the property; capturing the cursor is harmless.
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => ReportTargetClosed();

    private void CheckTargetAlive()
    {
        bool alive;
        try
        {
            alive = _isTargetAlive();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // a failed check says nothing about the target; try again on the next tick
        }

        if (!alive)
        {
            ReportTargetClosed();
        }
    }

    private void ReportTargetClosed() =>
        ReportFault(new CaptureError(
            CaptureErrorKind.TargetClosed,
            _target.Kind == CaptureTargetKind.Window
                ? "The captured window was closed."
                : "The captured display was disconnected or turned off."));

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                ProcessAvailableFrames(sender);
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                HandleFrameFailure(ex);
            }
        }
    }

    private void ProcessAvailableFrames(Direct3D11CaptureFramePool pool)
    {
        // Several frames may be queued if we fell behind. Keep only the newest and release the rest immediately.
        Direct3D11CaptureFrame? newest = null;
        Direct3D11CaptureFrame? next;
        while ((next = pool.TryGetNextFrame()) is not null)
        {
            newest?.Dispose();
            newest = next;
            _arrivedFrames++;
        }

        if (newest is null)
        {
            return;
        }

        using (newest)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastProcessedTimestamp != 0 && now - _lastProcessedTimestamp < _minFrameIntervalTicks)
            {
                return; // rate limit: drop without any GPU readback
            }

            var contentSize = newest.ContentSize;
            var frame = _processor.Process(
                newest,
                _target.Id,
                _arrivedFrames,
                _target.Kind == CaptureTargetKind.Display ? _target.Bounds : null,
                DateTimeOffset.UtcNow);

            if (frame is not null)
            {
                _lastProcessedTimestamp = now;
                _onFrame(frame);
            }

            if (contentSize.Width > 0 && contentSize.Height > 0
                && (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height))
            {
                // The target was resized (window resize, display mode change): match the pool to the new size.
                _poolSize = contentSize;
                pool.Recreate(_device.WinRtDevice, PoolPixelFormat, _options.FramePoolBufferCount, contentSize);
            }
        }
    }

    private void HandleFrameFailure(Exception exception)
    {
        if (CaptureErrorMapper.IsDeviceLost(exception) || DeviceIsRemoved())
        {
            ReportFault(CaptureErrorMapper.DeviceLostError($"{exception.GetType().Name} 0x{exception.HResult:X8}: {exception.Message}"));
            return;
        }

        _consecutiveFailures++;
        if (_consecutiveFailures >= _options.MaxConsecutiveFrameFailures)
        {
            ReportFault(new CaptureError(
                CaptureErrorKind.FrameProcessing,
                "Frames could not be processed repeatedly, so capture was stopped.")
            {
                Detail = $"{exception.GetType().Name} 0x{exception.HResult:X8}: {exception.Message}",
            });
        }
    }

    private bool DeviceIsRemoved()
    {
        try
        {
            return _device.GetDeviceRemovedReason() != 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    private void ReportFault(CaptureError error)
    {
        lock (_gate)
        {
            if (_disposed || _faultReported)
            {
                return;
            }

            _faultReported = true;
        }

        _onFault(error);
    }
}
