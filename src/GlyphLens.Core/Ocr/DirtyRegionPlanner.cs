namespace GlyphLens.Core.Ocr;

/// <summary>How a recognition pass was (or will be) done.</summary>
public enum OcrPassKind
{
    /// <summary>The whole frame was read.</summary>
    Full,

    /// <summary>Only the parts of the frame that changed (and the text lines they touch) were read again.</summary>
    Partial,

    /// <summary>No pixel changed: the previous result was reused.</summary>
    Unchanged,
}

/// <summary>What the incremental engine did in its most recent pass.</summary>
/// <param name="Kind">How the pass was done.</param>
/// <param name="ReadFraction">The share of the frame that was read, from 0 to 1 (1 for a full pass, 0 for an unchanged one).</param>
/// <param name="Rectangles">How many separate areas were read in a partial pass.</param>
/// <param name="Reason">For a full pass, why the whole frame was read (for example "first pass"); otherwise <see langword="null"/>.</param>
public sealed record OcrPassInfo(OcrPassKind Kind, double ReadFraction, int Rectangles, string? Reason);

/// <summary>An OCR engine that can say how its last pass was done.</summary>
public interface IOcrPassReporter
{
    /// <summary>The most recent pass, or <see langword="null"/> before the first one.</summary>
    OcrPassInfo? LastPass { get; }
}

/// <summary>Tunable limits of the incremental engine. The defaults are conservative: when in doubt the whole frame is read.</summary>
public sealed record IncrementalOcrOptions
{
    /// <summary>Edge of the tiles used to find what changed, in pixels.</summary>
    public int TileSize { get; init; } = 32;

    /// <summary>Changed tiles this many tiles apart (in either direction) or less are read together.</summary>
    public int ClusterGapTiles { get; init; } = 2;

    /// <summary>Margin added around every changed area before it is read, in pixels.</summary>
    public int Padding { get; init; } = 16;

    /// <summary>The smallest area that is read on its own, in pixels (recognisers do poorly on slivers).</summary>
    public int MinWidth { get; init; } = 96;

    /// <inheritdoc cref="MinWidth"/>
    public int MinHeight { get; init; } = 64;

    /// <summary>When the areas to read add up to more than this share of the frame, the whole frame is read instead.</summary>
    public double MaxReadFraction { get; init; } = 0.35;

    /// <summary>More separate areas than this and the whole frame is read instead.</summary>
    public int MaxRectangles { get; init; } = 3;

    /// <summary>After this many partial passes in a row the whole frame is read once, so an error can never persist.</summary>
    public int FullEveryPartialPasses { get; init; } = 12;

    /// <summary>The whole frame is also read when the last full pass is older than this.</summary>
    public TimeSpan FullAfter { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The outcome of planning: what to read for a frame whose changes are known.</summary>
/// <param name="Kind"><see cref="OcrPassKind.Full"/>, <see cref="OcrPassKind.Partial"/> or <see cref="OcrPassKind.Unchanged"/>.</param>
/// <param name="Rectangles">For a partial pass, the areas to read (disjoint, inside the frame); otherwise empty.</param>
/// <param name="ReadFraction">The share of the frame the areas cover.</param>
/// <param name="Reason">For a full plan, why.</param>
public sealed record OcrPlan(OcrPassKind Kind, IReadOnlyList<PixelRect> Rectangles, double ReadFraction, string? Reason);

/// <summary>
/// Decides which parts of a frame must be read again, given which tiles changed and where the previous pass found text.
/// </summary>
/// <remarks>
/// <para>
/// A changed area is widened by a margin and then by every text line of the previous result that it touches (a line is always read
/// as a whole, never cut), repeatedly, until it is stable. Areas that touch are merged. If that leaves too much of the frame, too
/// many separate areas, or nothing sensible, the answer is to read the whole frame. Scrolling, a page change or an animation therefore
/// always ends in a full read.
/// </para>
/// </remarks>
public static class DirtyRegionPlanner
{
    private const int MaxRefinements = 8;

    public static OcrPlan Plan(
        FrameDiffResult diff,
        int frameWidth,
        int frameHeight,
        IReadOnlyList<PixelRect> previousTextBounds,
        IncrementalOcrOptions options)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(previousTextBounds);
        ArgumentNullException.ThrowIfNull(options);

        if (diff.IsUnchanged)
        {
            return new OcrPlan(OcrPassKind.Unchanged, [], 0, null);
        }

        int totalTiles = diff.TilesX * diff.TilesY;
        if ((double)diff.DirtyCount / totalTiles > options.MaxReadFraction)
        {
            return Full("most of the frame changed");
        }

        var rectangles = Cluster(diff, options.ClusterGapTiles)
            .Select(r => r.InflateClamped(options.Padding, frameWidth, frameHeight).AtLeast(options.MinWidth, options.MinHeight, frameWidth, frameHeight))
            .ToList();

        for (int round = 0; round < MaxRefinements; round++)
        {
            bool changed = MergeIntersecting(rectangles);
            for (int i = 0; i < rectangles.Count; i++)
            {
                foreach (var text in previousTextBounds)
                {
                    if (rectangles[i].Intersects(text) && !Contains(rectangles[i], text))
                    {
                        rectangles[i] = rectangles[i].Union(text.InflateClamped(options.Padding / 2, frameWidth, frameHeight));
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        // Verify the result is stable: nothing left to merge, and no area cuts through a line of the previous result.
        if (MergeIntersecting(rectangles)
            || rectangles.Any(r => previousTextBounds.Any(t => r.Intersects(t) && !Contains(r, t))))
        {
            return Full("the areas to read kept growing into each other");
        }

        if (rectangles.Count > options.MaxRectangles)
        {
            return Full("too many separate changes");
        }

        double fraction = rectangles.Sum(r => (double)r.Area) / ((double)frameWidth * frameHeight);
        return fraction > options.MaxReadFraction
            ? Full("the changed areas cover most of the frame")
            : new OcrPlan(OcrPassKind.Partial, rectangles, fraction, null);
    }

    private static OcrPlan Full(string reason) => new(OcrPassKind.Full, [], 1.0, reason);

    private static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>Merges rectangles that intersect (or touch) until none do; returns whether anything was merged.</summary>
    private static bool MergeIntersecting(List<PixelRect> rectangles)
    {
        bool any = false;
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < rectangles.Count && !merged; i++)
            {
                for (int j = i + 1; j < rectangles.Count; j++)
                {
                    if (Touches(rectangles[i], rectangles[j]))
                    {
                        rectangles[i] = rectangles[i].Union(rectangles[j]);
                        rectangles.RemoveAt(j);
                        merged = true;
                        any = true;
                        break;
                    }
                }
            }
        }
        while (merged);

        return any;
    }

    private static bool Touches(PixelRect a, PixelRect b) =>
        a.X <= b.Right && b.X <= a.Right && a.Y <= b.Bottom && b.Y <= a.Bottom;

    /// <summary>Groups the changed tiles (tiles within <paramref name="gapTiles"/> of each other belong together) and returns each group's bounds in pixels.</summary>
    private static List<PixelRect> Cluster(FrameDiffResult diff, int gapTiles)
    {
        int tilesX = diff.TilesX;
        int tilesY = diff.TilesY;
        var parent = new int[tilesX * tilesY];
        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                if (!diff.IsDirty(tx, ty))
                {
                    continue;
                }

                int index = (ty * tilesX) + tx;
                for (int dy = 0; dy <= gapTiles; dy++)
                {
                    for (int dx = -gapTiles; dx <= gapTiles; dx++)
                    {
                        if (dy == 0 && dx <= 0)
                        {
                            continue; // only look forward: each pair is visited once
                        }

                        int nx = tx + dx;
                        int ny = ty + dy;
                        if (nx < 0 || nx >= tilesX || ny >= tilesY || !diff.IsDirty(nx, ny))
                        {
                            continue;
                        }

                        int a = Find(index);
                        int b = Find((ny * tilesX) + nx);
                        if (a != b)
                        {
                            parent[b] = a;
                        }
                    }
                }
            }
        }

        var bounds = new Dictionary<int, (int MinX, int MinY, int MaxX, int MaxY)>();
        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                if (!diff.IsDirty(tx, ty))
                {
                    continue;
                }

                int root = Find((ty * tilesX) + tx);
                bounds[root] = bounds.TryGetValue(root, out var b)
                    ? (Math.Min(b.MinX, tx), Math.Min(b.MinY, ty), Math.Max(b.MaxX, tx), Math.Max(b.MaxY, ty))
                    : (tx, ty, tx, ty);
            }
        }

        int tile = diff.TileSize;
        return [.. bounds.Values.Select(b => new PixelRect(b.MinX * tile, b.MinY * tile, (b.MaxX - b.MinX + 1) * tile, (b.MaxY - b.MinY + 1) * tile))];
    }
}
