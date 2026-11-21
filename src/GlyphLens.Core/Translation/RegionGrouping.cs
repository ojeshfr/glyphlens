using GlyphLens.Core.Models;

namespace GlyphLens.Core.Translation;

/// <summary>
/// A set of recognised lines that belong together (one chat message, one paragraph) and are translated as one text.
/// </summary>
/// <remarks>
/// A translation needs its context: a sentence split over two lines translates badly line by line, and a model sees each
/// OCR fragment out of context. The group keeps every member region (identity and coordinates) so a translation can be
/// mapped back to where the text is on screen. <see cref="Region"/> is the region translated as a unit: the only member
/// when the group has one, otherwise a merged region (the first member's id, the union of the bounds, the joined text, the
/// lowest confidence among the members that report one).
/// </remarks>
public sealed class RegionGroup
{
    public RegionGroup(IReadOnlyList<ScreenTextRegion> members, int readingOrder)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            throw new ArgumentException("A group needs at least one region.", nameof(members));
        }

        Members = members;
        ReadingOrder = readingOrder;
        Region = members.Count == 1 ? members[0] : Merge(members);
    }

    /// <summary>The recognised lines of the group, in reading order.</summary>
    public IReadOnlyList<ScreenTextRegion> Members { get; }

    /// <summary>Position of the group among all groups of the pass (0 = first).</summary>
    public int ReadingOrder { get; }

    /// <summary>The region that is translated for this group.</summary>
    public ScreenTextRegion Region { get; }

    private static ScreenTextRegion Merge(IReadOnlyList<ScreenTextRegion> members)
    {
        int left = members.Min(m => m.X);
        int top = members.Min(m => m.Y);
        int right = members.Max(m => m.X + m.Width);
        int bottom = members.Max(m => m.Y + m.Height);
        double? confidence = members.Where(m => m.Confidence is not null).Select(m => m.Confidence).DefaultIfEmpty(null).Min();
        var text = members[0].Text.Trim();
        for (int i = 1; i < members.Count; i++)
        {
            string next = members[i].Text.Trim();
            text += RegionGrouping.Separator(text, next) + next;
        }

        return new ScreenTextRegion(
            members[0].Id,
            text,
            left,
            top,
            right - left,
            bottom - top,
            members[0].DetectionSource,
            confidence,
            members[0].SourceLanguage,
            members.Max(m => m.Timestamp));
    }
}

/// <summary>Decides which recognised lines are translated together.</summary>
public interface IRegionGrouper
{
    /// <summary>Returns the groups in reading order. Every input region is in exactly one group.</summary>
    IReadOnlyList<RegionGroup> Group(IReadOnlyList<ScreenTextRegion> regions);
}

/// <summary>Every region is its own group (no merging).</summary>
public sealed class IdentityRegionGrouper : IRegionGrouper
{
    public IReadOnlyList<RegionGroup> Group(IReadOnlyList<ScreenTextRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return [.. regions.Select((region, index) => new RegionGroup([region], index))];
    }
}

/// <summary>
/// Groups lines by geometry: lines of one paragraph or chat bubble are close together vertically, similar in height and
/// aligned; a new paragraph or bubble starts after a larger gap, a change of size or a change of alignment. Pieces that
/// sit on one row (an OCR line split in two) are joined first.
/// </summary>
public sealed class ParagraphRegionGrouper : IRegionGrouper
{
    /// <summary>Largest gap between two lines of one paragraph, as a fraction of the (smaller) line height.</summary>
    public double MaxLineGap { get; init; } = 0.9;

    /// <summary>Largest ratio between the heights of two lines of one paragraph.</summary>
    public double MaxHeightRatio { get; init; } = 1.6;

    /// <summary>Largest gap between two pieces of one row, as a multiple of the row height.</summary>
    public double MaxPieceGap { get; init; } = 2.0;

    public IReadOnlyList<RegionGroup> Group(IReadOnlyList<ScreenTextRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
        {
            return [];
        }

        var lines = JoinRows([.. regions.OrderBy(r => r.Y + (r.Height / 2.0)).ThenBy(r => r.X)]);
        var groups = new List<List<ScreenTextRegion>>();
        List<ScreenTextRegion>? current = null;
        (int Left, int Top, int Right, int Bottom) previous = default;

        foreach (var line in lines)
        {
            var box = Bounds(line);
            if (current is not null && Continues(previous, box))
            {
                current.AddRange(line);
            }
            else
            {
                current = [.. line];
                groups.Add(current);
            }

            previous = box;
        }

        return [.. groups.Select((members, index) => new RegionGroup(members, index))];
    }

    /// <summary>Puts regions that share a row into rows, then splits each row into runs of pieces that sit close together (a wide gap is another column).</summary>
    private List<List<ScreenTextRegion>> JoinRows(List<ScreenTextRegion> ordered)
    {
        var rows = new List<List<ScreenTextRegion>>();
        foreach (var region in ordered)
        {
            if (rows.Count > 0 && SharesRow(rows[^1], region))
            {
                rows[^1].Add(region);
            }
            else
            {
                rows.Add([region]);
            }
        }

        var lines = new List<List<ScreenTextRegion>>();
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.X.CompareTo(b.X));
            var run = new List<ScreenTextRegion> { row[0] };
            for (int i = 1; i < row.Count; i++)
            {
                var last = run[^1];
                double smaller = Math.Min(last.Height, row[i].Height);
                if (row[i].X - (last.X + last.Width) <= smaller * MaxPieceGap)
                {
                    run.Add(row[i]);
                }
                else
                {
                    lines.Add(run);
                    run = [row[i]];
                }
            }

            lines.Add(run);
        }

        return lines;
    }

    private static bool SharesRow(List<ScreenTextRegion> row, ScreenTextRegion candidate)
    {
        var box = Bounds(row);
        int overlap = Math.Min(box.Bottom, candidate.Y + candidate.Height) - Math.Max(box.Top, candidate.Y);
        int smaller = Math.Min(box.Bottom - box.Top, candidate.Height);
        return overlap >= smaller * 0.6;
    }

    private bool Continues((int Left, int Top, int Right, int Bottom) above, (int Left, int Top, int Right, int Bottom) below)
    {
        double heightAbove = above.Bottom - above.Top;
        double heightBelow = below.Bottom - below.Top;
        double smaller = Math.Min(heightAbove, heightBelow);
        double ratio = Math.Max(heightAbove, heightBelow) / Math.Max(smaller, 1);
        double gap = below.Top - above.Bottom;
        if (ratio > MaxHeightRatio || gap > smaller * MaxLineGap || gap < -smaller * 0.5)
        {
            return false;
        }

        // Aligned: the left edges are close (a paragraph) or the lines overlap horizontally by at least half (centred text).
        bool leftAligned = Math.Abs(above.Left - below.Left) <= Math.Max(heightAbove, heightBelow) * 1.5;
        int overlap = Math.Min(above.Right, below.Right) - Math.Max(above.Left, below.Left);
        bool overlapping = overlap >= Math.Min(above.Right - above.Left, below.Right - below.Left) * 0.5;
        return leftAligned || overlapping;
    }

    private static (int Left, int Top, int Right, int Bottom) Bounds(IEnumerable<ScreenTextRegion> line)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var region in line)
        {
            left = Math.Min(left, region.X);
            top = Math.Min(top, region.Y);
            right = Math.Max(right, region.X + region.Width);
            bottom = Math.Max(bottom, region.Y + region.Height);
        }

        return (left, top, right, bottom);
    }
}

internal static class RegionGrouping
{
    /// <summary>The text between two joined lines: nothing next to a CJK character (those scripts put no spaces between words), otherwise one space.</summary>
    internal static string Separator(string before, string after)
    {
        if (before.Length == 0 || after.Length == 0)
        {
            return string.Empty;
        }

        return IsCjk(before[^1]) || IsCjk(after[0]) ? string.Empty : " ";
    }

    private static bool IsCjk(char c)
    {
        int v = c;
        return v is (>= 0x3000 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF);
    }
}
