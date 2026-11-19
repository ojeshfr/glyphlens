namespace GlyphLens.Core.Models;

/// <summary>
/// An axis-aligned rectangle in virtual-screen physical pixels.
/// </summary>
/// <remarks>
/// Coordinates are relative to the Windows virtual screen, so <see cref="X"/> and <see cref="Y"/>
/// may be negative on multi-monitor layouts. Conversion to per-monitor logical (DPI-scaled)
/// units is the responsibility of the Windows and presentation layers.
/// </remarks>
public readonly record struct ScreenRectangle
{
    public ScreenRectangle(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }
}
