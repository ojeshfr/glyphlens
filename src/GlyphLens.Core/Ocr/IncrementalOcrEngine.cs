using System.Buffers;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Ocr;

/// <summary>
/// Wraps an OCR engine so that a live feed of nearly identical frames is not read from scratch every time: when nothing changed the
/// previous result is reused, and when only a part of the frame changed (a new chat message, a clock) only that part, widened to
/// whole text lines, is read again.
/// </summary>
/// <remarks>
/// <para>
/// Correctness comes first. Change detection compares pixels exactly (see <see cref="FrameDiff"/>), so nothing is missed; the planner
/// (<see cref="DirtyRegionPlanner"/>) falls back to reading the whole frame whenever a partial read could be wrong or is not worthwhile:
/// the first pass, a different target, frame size or engine, a new capture, a large or scattered change (scrolling, page changes),
/// recognised text that touches the edge of an area read on its own, and after a fixed number of partial passes or time so that no
/// error can persist. The result of a partial pass is the previous result with the text lines inside the changed areas replaced.
/// </para>
/// <para>
/// The previous frame's pixels and recognised text are kept in memory only, for as long as capture runs; they are dropped when the
/// capture stops (<see cref="Reset"/>), when an engine call fails, and on disposal. Passes are serialised. Recognition of an
/// area is done by the wrapped engine on a copy of those pixels, so the caller's frame is never retained.
/// </para>
/// </remarks>
public sealed class IncrementalOcrEngine : IOcrEngine, IOcrPassReporter, IDisposable
{
    private readonly IOcrEngine _inner;
    private readonly IScreenCaptureService? _capture;
    private readonly Func<string?>? _engineKey;
    private readonly IncrementalOcrOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private byte[]? _previous;
    private string? _key;
    private long _lastSequence;
    private IReadOnlyList<ScreenTextRegion> _regions = [];
    private int _partialStreak;
    private DateTimeOffset _lastFull;
    private bool _valid;
    private int _resetRequested;
    private OcrPassInfo? _lastPass;
    private bool _disposed;

    /// <param name="inner">The engine that reads frames (or parts of them).</param>
    /// <param name="capture">When given, the saved state is dropped as soon as capture is no longer running.</param>
    /// <param name="engineKey">Identifies the engine setting (for example the chosen language); a change forces a full pass.</param>
    /// <param name="options">Limits; <see langword="null"/> for the conservative defaults.</param>
    /// <param name="timeProvider">The clock (for tests); <see langword="null"/> for the system clock.</param>
    public IncrementalOcrEngine(
        IOcrEngine inner,
        IScreenCaptureService? capture = null,
        Func<string?>? engineKey = null,
        IncrementalOcrOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _capture = capture;
        _engineKey = engineKey;
        _options = options ?? new IncrementalOcrOptions();
        _time = timeProvider ?? TimeProvider.System;

        if (_capture is not null)
        {
            _capture.StatusChanged += OnCaptureStatusChanged;
        }
    }

    /// <inheritdoc />
    public OcrPassInfo? LastPass => Volatile.Read(ref _lastPass);

    /// <summary>Forgets the previous frame and result, so the next pass reads the whole frame.</summary>
    public void Reset()
    {
        // If a pass is running it finishes first; it sees the request and starts the next one from scratch.
        Volatile.Write(ref _resetRequested, 1);
        if (_gate.Wait(0))
        {
            try
            {
                Invalidate();
            }
            finally
            {
                _ = _gate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_capture is not null)
        {
            _capture.StatusChanged -= OnCaptureStatusChanged;
        }

        Reset();
    }

    public async Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
            {
                Invalidate();
            }

            var metadata = frame.Metadata;
            string key = $"{metadata.TargetId}|{metadata.Width}x{metadata.Height}|{metadata.Stride}|{_engineKey?.Invoke()}";
            string? fullReason = FullReason(metadata, key);

            if (fullReason is null)
            {
                var pixels = frame.Pixels.Span;
                var diff = FrameDiff.Compare(_previous!, pixels, metadata.Width, metadata.Height, metadata.Stride, _options.TileSize);
                var plan = DirtyRegionPlanner.Plan(diff, metadata.Width, metadata.Height, [.. _regions.Select(Bounds)], _options);

                switch (plan.Kind)
                {
                    case OcrPassKind.Unchanged:
                        _lastSequence = metadata.Sequence;
                        Volatile.Write(ref _lastPass, new OcrPassInfo(OcrPassKind.Unchanged, 0, 0, null));
                        return _regions;

                    case OcrPassKind.Partial:
                        if (await TryReadPartialAsync(frame, plan, cancellationToken).ConfigureAwait(false) is { } merged)
                        {
                            return merged;
                        }

                        fullReason = "recognised text touched the edge of a changed area";
                        break;

                    default:
                        fullReason = plan.Reason;
                        break;
                }
            }

            return await ReadFullAsync(frame, key, fullReason ?? "full read", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private string? FullReason(FrameMetadata metadata, string key)
    {
        if (!_valid || _previous is null)
        {
            return "first pass";
        }

        if (metadata.PixelFormat != PixelFormat.Bgra8)
        {
            return "unsupported pixel format";
        }

        if (!string.Equals(key, _key, StringComparison.Ordinal))
        {
            return "the target, the frame size or the engine changed";
        }

        if (metadata.Sequence < _lastSequence)
        {
            return "a new capture started";
        }

        if (_partialStreak >= _options.FullEveryPartialPasses)
        {
            return "periodic full read";
        }

        return _time.GetUtcNow() - _lastFull >= _options.FullAfter ? "periodic full read" : null;
    }

    private async Task<IReadOnlyList<ScreenTextRegion>> ReadFullAsync(CapturedFrame frame, string key, string reason, CancellationToken cancellationToken)
    {
        IReadOnlyList<ScreenTextRegion> regions;
        try
        {
            regions = await _inner.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Invalidate();
            throw;
        }

        Remember(frame, [.. regions]);
        _key = key;
        _lastFull = _time.GetUtcNow();
        _partialStreak = 0;
        Volatile.Write(ref _lastPass, new OcrPassInfo(OcrPassKind.Full, 1.0, 0, reason));
        return _regions;
    }

    /// <summary>Reads the changed areas and merges them into the previous result; <see langword="null"/> when a full read is needed instead.</summary>
    private async Task<IReadOnlyList<ScreenTextRegion>?> TryReadPartialAsync(CapturedFrame frame, OcrPlan plan, CancellationToken cancellationToken)
    {
        var metadata = frame.Metadata;
        var merged = _regions.Where(r => !plan.Rectangles.Any(rect => rect.Intersects(Bounds(r)))).ToList();

        try
        {
            foreach (var rect in plan.Rectangles)
            {
                using var crop = Crop(frame, rect);
                var found = await _inner.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
                foreach (var region in found)
                {
                    if (TouchesInteriorEdge(region, rect, metadata.Width, metadata.Height))
                    {
                        return null;
                    }

                    merged.Add(Offset(region, rect.X, rect.Y));
                }
            }
        }
        catch
        {
            Invalidate();
            throw;
        }

        merged.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        Remember(frame, merged);
        _partialStreak++;
        Volatile.Write(ref _lastPass, new OcrPassInfo(OcrPassKind.Partial, plan.ReadFraction, plan.Rectangles.Count, null));
        return _regions;
    }

    private void Remember(CapturedFrame frame, IReadOnlyList<ScreenTextRegion> regions)
    {
        var metadata = frame.Metadata;
        int bytes = metadata.ByteLength;
        if (_previous is null || _previous.Length < bytes)
        {
            _previous = new byte[bytes];
        }

        frame.Pixels.Span.CopyTo(_previous);
        _lastSequence = metadata.Sequence;
        _regions = regions;
        _valid = true;
    }

    private void Invalidate()
    {
        _valid = false;
        _regions = [];
        _previous = null;
        _key = null;
        _partialStreak = 0;
    }

    private static PixelRect Bounds(ScreenTextRegion region) => new(region.X, region.Y, region.Width, region.Height);

    private static ScreenTextRegion Offset(ScreenTextRegion region, int dx, int dy) => new(
        region.Id,
        region.Text,
        region.X + dx,
        region.Y + dy,
        region.Width,
        region.Height,
        region.DetectionSource,
        region.Confidence,
        region.SourceLanguage,
        region.Timestamp);

    // Text that reaches the border of an area read on its own (where that border is not the frame's own edge) may be cut off.
    private static bool TouchesInteriorEdge(ScreenTextRegion region, PixelRect area, int frameWidth, int frameHeight) =>
        (region.X <= 1 && area.X > 0)
        || (region.Y <= 1 && area.Y > 0)
        || (region.X + region.Width >= area.Width - 1 && area.Right < frameWidth)
        || (region.Y + region.Height >= area.Height - 1 && area.Bottom < frameHeight);

    private static CapturedFrame Crop(CapturedFrame frame, PixelRect area)
    {
        var source = frame.Metadata;
        int rowBytes = area.Width * FrameMetadata.BytesPerPixel;
        var buffer = ArrayPool<byte>.Shared.Rent(rowBytes * area.Height);
        var pixels = frame.Pixels.Span;
        for (int row = 0; row < area.Height; row++)
        {
            pixels.Slice(((area.Y + row) * source.Stride) + (area.X * FrameMetadata.BytesPerPixel), rowBytes).CopyTo(buffer.AsSpan(row * rowBytes, rowBytes));
        }

        var metadata = new FrameMetadata(source.TargetId, area.Width, area.Height, rowBytes, source.PixelFormat, source.Timestamp, source.Sequence);
        return new CapturedFrame(metadata, buffer, static rented => ArrayPool<byte>.Shared.Return(rented));
    }

    private void OnCaptureStatusChanged(object? sender, CaptureStatusChangedEventArgs args)
    {
        if (args.CurrentStatus != CaptureStatus.Capturing)
        {
            Reset();
        }
    }
}
