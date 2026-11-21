namespace GlyphLens.App.Overlay;

/// <summary>A rectangle in physical (device) pixels of the virtual desktop. The origin may be negative (a monitor left of or above the primary one).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>The size of the overlap with <paramref name="other"/> in pixels (0 when they do not overlap).</summary>
    public (int Width, int Height) OverlapWith(PixelRect other)
    {
        int width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        int height = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return (Math.Max(width, 0), Math.Max(height, 0));
    }

    public long OverlapArea(PixelRect other)
    {
        var (w, h) = OverlapWith(other);
        return (long)w * h;
    }
}

/// <summary>
/// Decides where the overlay may be, so it is never lost after the display configuration changes (a monitor was unplugged, a
/// resolution or scale changed, a saved position belongs to a layout that no longer exists).
/// </summary>
/// <remarks>All values are physical pixels of the virtual desktop; the work areas exclude the taskbar.</remarks>
public static class OverlayPlacement
{
    /// <summary>The overlay counts as reachable when at least this much of it (its header) lies on a monitor.</summary>
    public const int MinReachableWidth = 120;
    public const int MinReachableHeight = 48;

    /// <summary>Gap to the screen edge when the overlay is placed automatically.</summary>
    public const int Margin = 24;

    /// <summary>
    /// Returns a rectangle for the overlay: the <paramref name="desired"/> one when it is usable, shifted and shrunk to fit on the
    /// monitor that holds most of it, or - when too little of it is on any monitor - the top-right corner of the primary monitor.
    /// </summary>
    /// <param name="desired">The wanted position and size.</param>
    /// <param name="workAreas">The usable area of every connected monitor (at least one).</param>
    /// <param name="primary">The usable area of the primary monitor.</param>
    /// <param name="minSize">The smallest allowed size.</param>
    /// <param name="maxWidth">The largest allowed width (a configurable limit).</param>
    public static PixelRect Fit(PixelRect desired, IReadOnlyList<PixelRect> workAreas, PixelRect primary, (int Width, int Height) minSize, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0)
        {
            workAreas = [primary];
        }

        int width = Math.Clamp(desired.Width, minSize.Width, Math.Max(minSize.Width, maxWidth));
        int height = Math.Max(desired.Height, minSize.Height);

        // The monitor that holds the most of the window. The window is reachable when a header-sized part of it is on that monitor;
        // it is then pulled fully inside. Otherwise (a monitor was disconnected, a layout changed) it is placed again.
        var candidate = new PixelRect(desired.X, desired.Y, width, height);
        var best = workAreas.MaxBy(area => candidate.OverlapArea(area));
        var (overlapW, overlapH) = best.OverlapWith(candidate);
        bool reachable = overlapW >= Math.Min(MinReachableWidth, candidate.Width) && overlapH >= Math.Min(MinReachableHeight, candidate.Height);

        var area2 = reachable ? best : primary;
        width = Math.Min(width, area2.Width);
        height = Math.Min(height, area2.Height);

        int x, y;
        if (reachable)
        {
            x = Math.Clamp(desired.X, area2.X, area2.Right - width);
            y = Math.Clamp(desired.Y, area2.Y, area2.Bottom - height);
        }
        else
        {
            x = Math.Max(area2.X, area2.Right - width - Margin);
            y = Math.Min(area2.Y + (Margin * 3), Math.Max(area2.Y, area2.Bottom - height));
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>The default placement: top-right of the primary monitor, <paramref name="size"/> big.</summary>
    public static PixelRect Default(PixelRect primary, (int Width, int Height) size) =>
        Fit(new PixelRect(primary.Right - size.Width - Margin, primary.Y + (Margin * 3), size.Width, size.Height), [primary], primary, (1, 1), int.MaxValue);
}
