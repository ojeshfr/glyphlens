using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public class OcrModelTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static ScreenTextRegion Region(double? confidence) =>
        new(Guid.NewGuid(), "text", 1, 2, 30, 10, DetectionSource.Ocr, confidence, "en-US", Stamp);

    [Fact]
    public void Confidence_MayBeAbsent_BecauseSomeEnginesDoNotReportOne()
    {
        Assert.Null(Region(null).Confidence);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Confidence_WhenGiven_KeepsItsValue(double value)
    {
        Assert.Equal(value, Region(value).Confidence);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Confidence_WhenGiven_MustStillBeInTheUnitRange(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Region(value));
    }

    [Fact]
    public void OcrException_CarriesItsKind_AndDefaultsToFailed()
    {
        var ex = new OcrException(OcrErrorKind.LanguageNotInstalled, "not installed");

        Assert.Equal(OcrErrorKind.LanguageNotInstalled, ex.Kind);
        Assert.Equal("not installed", ex.Message);
        Assert.Equal(OcrErrorKind.Failed, new OcrException("boom").Kind);
        Assert.Equal(OcrErrorKind.Failed, new OcrException().Kind);
    }

    [Fact]
    public void OcrException_KeepsTheInnerException()
    {
        var inner = new InvalidOperationException("inner");

        Assert.Same(inner, new OcrException("outer", inner).InnerException);
        Assert.Same(inner, new OcrException(OcrErrorKind.Failed, "outer", inner).InnerException);
    }

    [Fact]
    public void OcrLanguage_IsAValueRecord()
    {
        Assert.Equal(new OcrLanguage("en-US", "English"), new OcrLanguage("en-US", "English"));
        Assert.NotEqual(new OcrLanguage("en-US", "English"), new OcrLanguage("de-DE", "German"));
    }
}
