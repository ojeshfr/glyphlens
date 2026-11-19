using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public class LanguageCodeTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("fil")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hant")]
    [InlineData("sr-Latn-RS")]
    public void IsValid_AcceptsLanguageCodes(string code)
    {
        Assert.True(LanguageCode.IsValid(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("EN")]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("en-")]
    [InlineData(" en")]
    public void IsValid_RejectsMalformedCodes(string? code)
    {
        Assert.False(LanguageCode.IsValid(code));
    }

    [Fact]
    public void Validate_ReturnsValueOrThrowsWithParameterName()
    {
        Assert.Equal("de", LanguageCode.Validate("de", "lang"));

        var ex = Assert.Throws<ArgumentException>(() => LanguageCode.Validate("xx_YY", "lang"));
        Assert.Equal("lang", ex.ParamName);
    }

    [Fact]
    public void English_IsAValidCode()
    {
        Assert.True(LanguageCode.IsValid(LanguageCode.English));
    }
}
