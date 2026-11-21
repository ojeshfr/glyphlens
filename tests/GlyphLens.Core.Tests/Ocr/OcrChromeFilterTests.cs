using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;

namespace GlyphLens.Core.Tests.Ocr;

public sealed class OcrChromeFilterTests
{
    private const int Width = 560;
    private const int Height = 687;

    private static ScreenTextRegion Region(string text, int x, int y, int width = 80, int height = 24) =>
        new(Guid.NewGuid(), text, x, y, width, height, DetectionSource.Ocr, null, null, DateTimeOffset.UnixEpoch);

    private static OcrChromeFilterResult Filter(string? title, params ScreenTextRegion[] regions) =>
        OcrChromeFilter.Filter(regions, Width, Height, title);

    [Fact]
    public void TheStripIsASmallFractionOfTheFrame_WithinLimits()
    {
        Assert.Equal(32, OcrChromeFilter.StripHeight(100));
        Assert.Equal(48, OcrChromeFilter.StripHeight(687));
        Assert.Equal(72, OcrChromeFilter.StripHeight(4000));
    }

    [Fact]
    public void ACloseButtonGlyph_InTheTopRight_IsSetAside()
    {
        var x = Region("X", 520, 8, 22, 25);

        var result = Filter(null, x);

        Assert.Empty(result.Kept);
        Assert.Equal(1, result.Skipped);
    }

    [Theory]
    [InlineData("×")]
    [InlineData("✕")]
    [InlineData("-")]
    [InlineData("□")]
    [InlineData("口")]
    [InlineData("- □")]
    public void OtherWindowButtonLookAlikes_AreSetAside(string glyphs)
    {
        var result = Filter(null, Region(glyphs, 500, 6, 40, 24));

        Assert.Empty(result.Kept);
    }

    [Fact]
    public void AnXInTheBodyOrOnTheLeft_IsContent()
    {
        var body = Region("X", 520, 200, 22, 25);
        var left = Region("X", 20, 8, 22, 25);

        var result = Filter(null, body, left);

        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void ARegionThatReachesBelowTheStrip_IsContent()
    {
        var tall = Region("X", 520, 20, 22, 60);

        Assert.Single(Filter(null, tall).Kept);
    }

    [Fact]
    public void ARegionAboveTheFrame_IsLeftAlone()
    {
        Assert.Single(Filter(null, Region("X", 520, -3, 22, 25)).Kept);
    }

    [Theory]
    [InlineData(":")]
    [InlineData(".")]
    [InlineData("|")]
    [InlineData("··")]
    public void PunctuationOnly_InTheTitleStrip_IsAnIconMisread_AnywhereAcrossTheStrip(string text) =>
        Assert.Empty(Filter(null, Region(text, 13, 10, 23, 22)).Kept);

    [Fact]
    public void PunctuationOnly_OutsideTheTitleStrip_IsContent() =>
        Assert.Single(Filter(null, Region(":", 13, 300, 23, 22)).Kept);

    [Fact]
    public void ALongRunOfSymbolsInTheStrip_IsNotAnIconMisread() =>
        Assert.Single(Filter(null, Region("-----", 20, 6, 200, 24)).Kept);

    [Fact]
    public void ThreeSymbolsInTheRightThirdWithoutSpaces_AreSetAsideAsPunctuation_ButNotAsButtonGlyphs()
    {
        // "---" has no letter or digit: it is punctuation in the title strip, so it is set aside by the icon rule.
        Assert.Empty(Filter(null, Region("---", 500, 6, 40, 24)).Kept);
    }

    [Fact]
    public void ARealWordThatStartsWithAnX_IsContent() =>
        Assert.Single(Filter(null, Region("Xi", 500, 6, 40, 24)).Kept);

    [Fact]
    public void TheWindowsOwnTitle_InTheStrip_IsSetAside()
    {
        var title = Region(": GlyphLensChineseTarget", 13, 10, 227, 23);
        var other = Region("今晚有空吗", 40, 90, 200, 24);

        var result = Filter("GlyphLensChineseTarget", title, other);

        Assert.Equal([other], result.Kept);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void ATitleShownOutsideTheStrip_IsContent()
    {
        var elsewhere = Region("GlyphLensChineseTarget", 40, 300, 227, 23);

        Assert.Single(Filter("GlyphLensChineseTarget", elsewhere).Kept);
    }

    [Fact]
    public void ATitleOfTwoHanCharacters_IsRecognised()
    {
        var title = Region("微信", 240, 8, 50, 24);

        Assert.Empty(Filter("微信", title).Kept);
    }

    [Fact]
    public void ShortFragmentsNeverMatchATitle()
    {
        var a = Region("a", 10, 8, 10, 24);

        Assert.Single(Filter("Calculator", a).Kept);
    }

    [Fact]
    public void ALongerTextThatMerelyContainsTheTitle_IsContent()
    {
        var longer = Region("Calculator results for tomorrow", 10, 8, 300, 24);

        Assert.Single(Filter("Calculator", longer).Kept);
    }

    [Fact]
    public void ChineseTextInTheStrip_ThatIsNotTheTitle_IsContent()
    {
        var chinese = Region("今天天气很好", 10, 8, 200, 24);

        Assert.Single(Filter("Notes", chinese).Kept);
    }

    [Fact]
    public void ADisplayCapture_HasNoTitleToMatch()
    {
        var text = Region("Dashboard", 10, 8, 120, 24);

        Assert.Single(Filter(null, text).Kept);
    }

    [Fact]
    public void NothingToFilter_ReturnsTheSameList()
    {
        var empty = Array.Empty<ScreenTextRegion>();

        var result = OcrChromeFilter.Filter(empty, Width, Height, "Title");

        Assert.Same(empty, result.Kept);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void WhenNothingIsSetAside_TheOriginalListIsReturned()
    {
        var regions = new[] { Region("Hello there", 40, 200) };

        var result = OcrChromeFilter.Filter(regions, Width, Height, "Title");

        Assert.Same(regions, result.Kept);
    }

    [Fact]
    public void OrderIsPreserved()
    {
        var a = Region("first line", 40, 100);
        var chrome = Region("X", 520, 8, 22, 25);
        var b = Region("second line", 40, 140);

        Assert.Equal([a, b], Filter(null, a, chrome, b).Kept);
    }
}
