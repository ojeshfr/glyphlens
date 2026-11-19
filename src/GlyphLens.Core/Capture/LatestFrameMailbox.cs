using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Capture;

/// <summary>
/// Single-slot, latest-wins implementation of <see cref="IFrameSource"/>.
/// </summary>
/// <remarks>
/// The producer calls <see cref="Publish"/> and hands over ownership of the frame; the previous frame's
/// producer reference is released at that moment. Consumers acquire their own references, so a frame
/// being processed stays valid until the consumer disposes it even after it was replaced.
/// All operations are thread-safe and never block on consumers.
/// </remarks>
public sealed class LatestFrameMailbox : IFrameSource, IDisposable
{
    private readonly object _gate = new();
    private CapturedFrame? _latest;
    private long _version;
    private bool _disposed;
    private TaskCompletionSource _signal = NewSignal();

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public FrameMetadata? LatestMetadata
    {
        get
        {
            lock (_gate)
            {
                return _latest?.Metadata;
            }
        }
    }

    /// <summary>Publishes <paramref name="frame"/> as the latest frame, taking ownership of the caller's reference.</summary>
    /// <returns>The version assigned to the frame, or 0 when the mailbox is disposed (the frame is then disposed).</returns>
    public long Publish(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        CapturedFrame? previous;
        TaskCompletionSource signal;
        long version;

        lock (_gate)
        {
            if (_disposed)
            {
                previous = frame;
                signal = _signal;
                version = 0;
            }
            else
            {
                previous = _latest;
                _latest = frame;
                version = ++_version;
                signal = _signal;
                _signal = NewSignal();
            }
        }

        previous?.Dispose();
        if (version != 0)
        {
            signal.TrySetResult();
        }

        return version;
    }

    /// <summary>Drops the current frame (for example when a capture session ends) without changing <see cref="Version"/>.</summary>
    public void Clear()
    {
        CapturedFrame? previous;
        lock (_gate)
        {
            previous = _latest;
            _latest = null;
        }

        previous?.Dispose();
    }

    public LatestFrame? TryAcquireLatest()
    {
        lock (_gate)
        {
            return _latest is null ? null : new LatestFrame(_latest.Retain(), _version);
        }
    }

    public async ValueTask<LatestFrame?> WaitForNewerAsync(long afterVersion, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }

                if (_latest is not null && _version > afterVersion)
                {
                    return new LatestFrame(_latest.Retain(), _version);
                }

                signal = _signal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the current frame and wakes all waiters, which then observe a disposed source.</summary>
    public void Dispose()
    {
        CapturedFrame? previous;
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            previous = _latest;
            _latest = null;
            signal = _signal;
        }

        previous?.Dispose();
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
