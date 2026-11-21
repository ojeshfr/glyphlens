using GlyphLens.Core.Models;

namespace GlyphLens.Core.Ocr;

/// <summary>Which tiles of a frame differ from the previous frame.</summary>
/// <param name="TilesX">Tiles per row.</param>
/// <param name="TilesY">Tile rows.</param>
/// <param name="TileSize">Edge of a tile in pixels (tiles at the right and bottom edges may be smaller).</param>
/// <param name="Dirty">Row-major flags, <c>TilesX * TilesY</c> long.</param>
/// <param name="DirtyCount">Number of tiles flagged.</param>
public sealed record FrameDiffResult(int TilesX, int TilesY, int TileSize, bool[] Dirty, int DirtyCount)
{
    public bool IsDirty(int tx, int ty) => Dirty[(ty * TilesX) + tx];

    /// <summary>Whether no pixel changed.</summary>
    public bool IsUnchanged => DirtyCount == 0;
}

/// <summary>
/// Compares two frames of the same size byte for byte and reports the tiles that differ. Exact (no sampling, no hashing), so a change
/// of a single pixel is never missed.
/// </summary>
public static class FrameDiff
{
    private const int BytesPerPixel = FrameMetadata.BytesPerPixel;

    /// <param name="previous">The earlier frame's pixels (<paramref name="stride"/> bytes per row).</param>
    /// <param name="current">The later frame's pixels, same layout.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="stride">Bytes per row (at least <c>width * 4</c>; the padding bytes are not compared).</param>
    /// <param name="tileSize">Edge of a tile in pixels.</param>
    public static FrameDiffResult Compare(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current, int width, int height, int stride, int tileSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * BytesPerPixel);
        long needed = ((long)stride * (height - 1)) + ((long)width * BytesPerPixel);
        if (previous.Length < needed || current.Length < needed)
        {
            throw new ArgumentException("A pixel buffer is smaller than the frame it describes.");
        }

        int tilesX = (width + tileSize - 1) / tileSize;
        int tilesY = (height + tileSize - 1) / tileSize;
        var dirty = new bool[tilesX * tilesY];
        int dirtyCount = 0;
        int rowBytes = width * BytesPerPixel;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            if (previous.Slice(rowStart, rowBytes).SequenceEqual(current.Slice(rowStart, rowBytes)))
            {
                continue; // the whole row is identical: no tile is touched by it
            }

            int tileRow = (y / tileSize) * tilesX;
            for (int tx = 0; tx < tilesX; tx++)
            {
                if (dirty[tileRow + tx])
                {
                    continue;
                }

                int x0 = tx * tileSize;
                int x1 = Math.Min(width, x0 + tileSize);
                int offset = rowStart + (x0 * BytesPerPixel);
                int length = (x1 - x0) * BytesPerPixel;
                if (!previous.Slice(offset, length).SequenceEqual(current.Slice(offset, length)))
                {
                    dirty[tileRow + tx] = true;
                    dirtyCount++;
                }
            }
        }

        return new FrameDiffResult(tilesX, tilesY, tileSize, dirty, dirtyCount);
    }
}
