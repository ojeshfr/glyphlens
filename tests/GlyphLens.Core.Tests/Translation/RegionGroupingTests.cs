using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.Core.Tests.Translation;

public sealed class RegionGroupingTests
{
    private static readonly ParagraphRegionGrouper Grouper = new();

    private static ScreenTextRegion Line(string text, int x, int y, int width = 300, int height = 28, double? confidence = null) =>
        new(Guid.NewGuid(), text, x, y, width, height, DetectionSource.Ocr, confidence, null, DateTimeOffset.UnixEpoch);

    private static string[][] Texts(IReadOnlyList<RegionGroup> groups) => [.. groups.Select(g => g.Members.Select(m => m.Text).ToArray())];

    [Fact]
    public void LinesOfOneParagraph_AreOneGroup_TranslatedAsOneText()
    {
        var lines = new[]
        {
            Line("随着人工智能技术的快速发展，越来越多的企业开始将其应用到日常", 24, 24, 640),
            Line("工作中。这不仅提高了工作效率，也带来了新的挑战，例如数据安全和", 24, 52, 640),
            Line("隐私保护问题。因此，企业必须建立完善的管理制度。", 24, 80, 520),
        };

        var group = Assert.Single(Grouper.Group(lines));

        Assert.Equal(3, group.Members.Count);
        Assert.Equal(string.Concat(lines.Select(l => l.Text)), group.Region.Text); // Chinese lines join without spaces
        Assert.Equal((24, 24, 640, 84), (group.Region.X, group.Region.Y, group.Region.Width, group.Region.Height));
        Assert.Equal(lines[0].Id, group.Region.Id);
    }

    [Fact]
    public void EnglishLines_AreJoinedWithASpace()
    {
        var group = Assert.Single(Grouper.Group([Line("This is the first", 10, 10), Line("half of a sentence.", 10, 38)]));

        Assert.Equal("This is the first half of a sentence.", group.Region.Text);
    }

    [Fact]
    public void SeparateChatBubbles_AreSeparateGroups()
    {
        // Bubble padding and the gap between bubbles leave a gap of about 1.5 line heights.
        var lines = new[]
        {
            Line("你今天晚上有空吗？我们一起去吃饭吧。", 34, 30, 380, 30),
            Line("我刚下班，现在在地铁上，大概二十分钟后到。", 34, 110, 450, 30),
            Line("在吗？", 34, 190, 60, 30),
        };

        Assert.Equal(3, Grouper.Group(lines).Count);
    }

    [Fact]
    public void ATwoLineBubble_IsOneGroup_AndAnotherBubbleBelowIsNot()
    {
        var lines = new[]
        {
            Line("这家餐厅的菜很好吃，", 34, 30, 220, 30),
            Line("就是价格有点贵。", 34, 62, 180, 30),
            Line("好的", 34, 150, 50, 30),
        };

        Assert.Equal([["这家餐厅的菜很好吃，", "就是价格有点贵。"], ["好的"]], Texts(Grouper.Group(lines)));
    }

    [Fact]
    public void ATitleAboveBodyText_IsNotMergedWithIt()
    {
        var lines = new[] { Line("设置", 20, 10, 80, 40), Line("您确定要删除这个文件吗？", 20, 60, 260, 20) };

        Assert.Equal(2, Grouper.Group(lines).Count);
    }

    [Fact]
    public void PiecesOnOneRow_AreJoinedIntoOneLine()
    {
        var lines = new[] { Line("请把 PPT", 20, 20, 90), Line("发给我", 120, 21, 60), Line("明天要用", 190, 20, 80) };

        var group = Assert.Single(Grouper.Group(lines));

        Assert.Equal(3, group.Members.Count);
        Assert.Equal("请把 PPT发给我明天要用", group.Region.Text);
    }

    [Fact]
    public void TwoColumnsSideBySide_AreNotMerged()
    {
        var lines = new[] { Line("左边的文字", 20, 20, 150), Line("右边的文字", 700, 20, 150) };

        Assert.Equal(2, Grouper.Group(lines).Count);
    }

    [Fact]
    public void LinesFarApartVertically_AreSeparate()
    {
        Assert.Equal(2, Grouper.Group([Line("第一段", 20, 20), Line("第二段", 20, 400)]).Count);
    }

    [Fact]
    public void GroupsAreInReadingOrder_RegardlessOfInputOrder()
    {
        var bottom = Line("最后", 20, 300, 100);
        var top = Line("最先", 20, 20, 100);
        var middle = Line("中间", 20, 160, 100);

        var groups = Grouper.Group([bottom, top, middle]);

        Assert.Equal([top.Id, middle.Id, bottom.Id], groups.Select(g => g.Region.Id));
        Assert.Equal([0, 1, 2], groups.Select(g => g.ReadingOrder));
    }

    [Fact]
    public void EveryRegionIsInExactlyOneGroup_WithItsIdentityAndCoordinatesKept()
    {
        var lines = new[]
        {
            Line("第一行", 24, 24, 200), Line("第二行", 24, 52, 200), Line("别的消息", 24, 200, 200), Line("单独", 500, 20, 80),
        };

        var groups = Grouper.Group(lines);

        var members = groups.SelectMany(g => g.Members).ToList();
        Assert.Equal(lines.Length, members.Count);
        Assert.Equal(lines.Select(l => l.Id).Order(), members.Select(m => m.Id).Order());
        Assert.All(members, m => Assert.Contains(lines, l => ReferenceEquals(l, m))); // the very same regions, untouched
    }

    [Fact]
    public void TheMergedConfidence_IsTheLowestReportedOne_AndNullWhenNoneReportsOne()
    {
        var withScores = Assert.Single(Grouper.Group([Line("甲", 10, 10, 30, 28, 0.99), Line("乙", 10, 38, 30, 28, 0.80)]));
        var withoutScores = Assert.Single(Grouper.Group([Line("甲", 10, 10), Line("乙", 10, 38)]));

        Assert.Equal(0.80, withScores.Region.Confidence);
        Assert.Null(withoutScores.Region.Confidence);
    }

    [Fact]
    public void ASingleRegion_IsItsOwnGroup_AndTheSameObject()
    {
        var only = Line("你好", 10, 10, 60);

        var group = Assert.Single(Grouper.Group([only]));

        Assert.Same(only, group.Region);
    }

    [Fact]
    public void NoRegions_NoGroups() => Assert.Empty(Grouper.Group([]));

    [Fact]
    public void IdentityGrouper_KeepsEveryRegionOnItsOwn()
    {
        var lines = new[] { Line("第一行", 24, 24), Line("第二行", 24, 52) };

        var groups = new IdentityRegionGrouper().Group(lines);

        Assert.Equal(2, groups.Count);
        Assert.Same(lines[0], groups[0].Region);
        Assert.Equal([0, 1], groups.Select(g => g.ReadingOrder));
    }

    [Fact]
    public void AGroupNeedsMembers() => Assert.Throws<ArgumentException>(() => new RegionGroup([], 0));
}
