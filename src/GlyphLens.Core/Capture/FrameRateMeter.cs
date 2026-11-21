namespace GlyphLens.Core.Capture;

/// <summary>
/// Measures how many frames per second a frame source actually delivered, from the frame counter and the clock: nothing is assumed
/// or configured.
/// </summary>
/// <remarks>
/// <para>
/// Feed it the source's version (it increases by one per published frame) every so often. The rate is the number of frames since the
/// oldest sample still inside the window, divided by the time since that sample. Windows Graphics Capture delivers a frame only when the
/// picture changes, so a static screen legitimately measures 0.
/// </para>
/// </remarks>
public sealed class FrameRateMeter
{
    private readonly long _windowTicks;
    private readonly Queue<(long Version, long Timestamp)> _samples = new();

    /// <param name="window">How far back the rate is measured (at least one second is sensible).</param>
    public FrameRateMeter(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _windowTicks = window.Ticks;
    }

    /// <summary>Records a sample and returns the measured rate, or <see langword="null"/> until two samples span some time.</summary>
    /// <param name="version">The frame source's version now.</param>
    /// <param name="timestampTicks">The clock now, in <see cref="TimeSpan"/> ticks (any monotonic origin).</param>
    public double? Add(long version, long timestampTicks)
    {
        if (_samples.Count > 0 && (timestampTicks < _samples.Peek().Timestamp || version < _samples.Peek().Version))
        {
            _samples.Clear(); // the clock or the counter went back (a new session): start over
        }

        _samples.Enqueue((version, timestampTicks));
        while (_samples.Count > 2 && timestampTicks - _samples.ElementAt(1).Timestamp >= _windowTicks)
        {
            _ = _samples.Dequeue();
        }

        var oldest = _samples.Peek();
        long elapsed = timestampTicks - oldest.Timestamp;
        return elapsed <= 0 ? null : (version - oldest.Version) * (double)TimeSpan.TicksPerSecond / elapsed;
    }

    /// <summary>Forgets every sample.</summary>
    public void Reset() => _samples.Clear();
}
