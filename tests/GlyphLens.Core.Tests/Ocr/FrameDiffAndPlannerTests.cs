using GlyphLens.Core.Ocr;

namespace GlyphLens.Core.Tests.Ocr;

public sealed class FrameDiffAndPlannerTests
{
    private const int Tile = 32;
    private static readonly IncrementalOcrOptions Options = new();

    private static byte[] Frame(int width, int height, int stride) => new byte[stride * height];

    [Fact]
    public void IdenticalFrames_HaveNoDirtyTile()
    {
        var a = Frame(100, 70, 400);
        var b = (byte[])a.Clone();

        var diff = FrameDiff.Compare(a, b, 100, 70, 400, Tile);

        Assert.True(diff.IsUnchanged);
        Assert.Equal(4, diff.TilesX);
        Assert.Equal(3, diff.TilesY);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(31, 31, 0, 0)]
    [InlineData(32, 31, 1, 0)]
    [InlineData(99, 69, 3, 2)]
    public void AnySinglePixel_FlagsExactlyItsTile(int x, int y, int tileX, int tileY)
    {
        var a = Frame(100, 70, 400);
        var b = (byte[])a.Clone();
        b[(y * 400) + (x * 4) + 1] = 1;

        var diff = FrameDiff.Compare(a, b, 100, 70, 400, Tile);

        Assert.Equal(1, diff.DirtyCount);
        Assert.True(diff.IsDirty(tileX, tileY));
    }

    [Fact]
    public void ARowPaddingChange_IsNotAPixelChange()
    {
        var a = Frame(100, 70, 432); // 32 bytes of padding per row
        var b = (byte[])a.Clone();
        b[(10 * 432) + 410] = 99;

        Assert.True(FrameDiff.Compare(a, b, 100, 70, 432, Tile).IsUnchanged);
    }

    [Fact]
    public void ABufferThatIsTooSmall_Throws()
    {
        var small = new byte[100];

        _ = Assert.Throws<ArgumentException>(() => FrameDiff.Compare(small, small, 100, 70, 400, Tile));
    }

    private static FrameDiffResult DirtyTiles(int width, int height, params (int X, int Y)[] pixels)
    {
        int stride = width * 4;
        var a = Frame(width, height, stride);
        var b = (byte[])a.Clone();
        foreach (var (x, y) in pixels)
        {
            b[(y * stride) + (x * 4)] = 1;
        }

        return FrameDiff.Compare(a, b, width, height, stride, Tile);
    }

    [Fact]
    public void Planner_UnchangedFrame_NeedsNoReading()
    {
        var plan = DirtyRegionPlanner.Plan(DirtyTiles(400, 600), 400, 600, [], Options);

        Assert.Equal(OcrPassKind.Unchanged, plan.Kind);
    }

    [Fact]
    public void Planner_ASmallChange_IsOnePaddedArea_NotSmallerThanTheMinimum()
    {
        var plan = DirtyRegionPlanner.Plan(DirtyTiles(400, 600, (200, 300)), 400, 600, [], Options);

        Assert.Equal(OcrPassKind.Partial, plan.Kind);
        var area = Assert.Single(plan.Rectangles);
        Assert.True(area.Width >= Options.MinWidth && area.Height >= Options.MinHeight);
        Assert.True(area.X <= 200 && area.Right > 200 && area.Y <= 300 && area.Bottom > 300);
        Assert.InRange(plan.ReadFraction, 0.0, 0.2);
    }

    [Fact]
    public void Planner_AChangeThatTouchesALine_ReadsTheLineAsAWhole()
    {
        var line = new PixelRect(20, 280, 300, 24);

        var plan = DirtyRegionPlanner.Plan(DirtyTiles(400, 600, (310, 300)), 400, 600, [line], Options);

        var area = Assert.Single(plan.Rectangles);
        Assert.True(area.X <= line.X && area.Right >= line.Right && area.Y <= line.Y && area.Bottom >= line.Bottom);
    }

    [Fact]
    public void Planner_NeverCutsThroughALineItDoesNotContain()
    {
        var lines = new List<PixelRect> { new(20, 100, 300, 24), new(20, 130, 300, 24), new(20, 160, 300, 24) };

        var plan = DirtyRegionPlanner.Plan(DirtyTiles(400, 600, (30, 140)), 400, 600, lines, Options);

        Assert.Equal(OcrPassKind.Partial, plan.Kind);
        foreach (var area in plan.Rectangles)
        {
            foreach (var line in lines.Where(area.Intersects))
            {
                Assert.True(line.X >= area.X && line.Y >= area.Y && line.Right <= area.Right && line.Bottom <= area.Bottom);
            }
        }
    }

    [Fact]
    public void Planner_AChangeOverMostOfTheFrame_IsFull()
    {
        var pixels = Enumerable.Range(0, 400 / Tile).SelectMany(tx => Enumerable.Range(0, 600 / Tile).Select(ty => (X: tx * Tile, Y: ty * Tile))).ToArray();

        var plan = DirtyRegionPlanner.Plan(DirtyTiles(400, 600, pixels), 400, 600, [], Options);

        Assert.Equal(OcrPassKind.Full, plan.Kind);
        Assert.Equal("most of the frame changed", plan.Reason);
    }

    [Fact]
    public void Planner_NearbyChanges_AreReadTogether_FarOnesSeparately()
    {
        var together = DirtyRegionPlanner.Plan(DirtyTiles(400, 600, (50, 100), (110, 100)), 400, 600, [], Options);
        var apart = DirtyRegionPlanner.Plan(DirtyTiles(400, 600, (20, 20), (350, 560)), 400, 600, [], Options);

        Assert.Single(together.Rectangles);
        Assert.Equal(2, apart.Rectangles.Count);
    }

    [Fact]
    public void PixelRect_InflateAndMinimumStayInsideTheFrame()
    {
        var corner = new PixelRect(0, 0, 10, 10).InflateClamped(16, 400, 600);
        var minimum = new PixelRect(390, 590, 10, 10).AtLeast(96, 64, 400, 600);

        Assert.Equal(new PixelRect(0, 0, 26, 26), corner);
        Assert.Equal(new PixelRect(304, 536, 96, 64), minimum);
    }
}
