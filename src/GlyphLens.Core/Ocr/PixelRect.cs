namespace GlyphLens.Core.Ocr;

/// <summary>An axis-aligned rectangle in frame pixels (origin top-left, exclusive right and bottom edges).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public long Area => (long)Width * Height;

    public bool Intersects(PixelRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public PixelRect Union(PixelRect other)
    {
        int left = Math.Min(X, other.X);
        int top = Math.Min(Y, other.Y);
        return new PixelRect(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }

    /// <summary>Grows the rectangle by <paramref name="amount"/> on every side and clips it to a frame of the given size.</summary>
    public PixelRect InflateClamped(int amount, int frameWidth, int frameHeight)
    {
        int left = Math.Max(0, X - amount);
        int top = Math.Max(0, Y - amount);
        int right = Math.Min(frameWidth, Right + amount);
        int bottom = Math.Min(frameHeight, Bottom + amount);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Grows the rectangle (inside the frame) until it is at least the given size, keeping it centred where possible.</summary>
    public PixelRect AtLeast(int minWidth, int minHeight, int frameWidth, int frameHeight)
    {
        int width = Math.Min(Math.Max(Width, minWidth), frameWidth);
        int height = Math.Min(Math.Max(Height, minHeight), frameHeight);
        int x = Math.Clamp(X - ((width - Width) / 2), 0, frameWidth - width);
        int y = Math.Clamp(Y - ((height - Height) / 2), 0, frameHeight - height);
        return new PixelRect(x, y, width, height);
    }
}
