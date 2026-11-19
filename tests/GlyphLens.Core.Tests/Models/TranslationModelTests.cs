using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public class TranslationModelTests
{
    [Fact]
    public void TranslationRequest_WithValidValues_SetsProperties()
    {
        var request = new TranslationRequest("Hola mundo", "es", "en");

        Assert.Equal("Hola mundo", request.Text);
        Assert.Equal("es", request.SourceLanguage);
        Assert.Equal("en", request.TargetLanguage);
    }

    [Fact]
    public void TranslationRequest_AllowsSourceLanguageToBeDetected()
    {
        Assert.Null(new TranslationRequest("Hola", null, LanguageCode.English).SourceLanguage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void TranslationRequest_RejectsBlankText(string text)
    {
        Assert.Throws<ArgumentException>(() => new TranslationRequest(text, "es", "en"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("EN")]
    [InlineData("english")]
    public void TranslationRequest_RejectsInvalidTargetLanguage(string target)
    {
        var ex = Assert.Throws<ArgumentException>(() => new TranslationRequest("Hola", "es", target));
        Assert.Equal("targetLanguage", ex.ParamName);
    }

    [Fact]
    public void TranslationRequest_RejectsInvalidSourceLanguage()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TranslationRequest("Hola", "e", "en"));
        Assert.Equal("sourceLanguage", ex.ParamName);
    }

    [Fact]
    public void TranslationResult_WithValidValues_SetsProperties()
    {
        var result = new TranslationResult("Hola", "Hello", "es", "en", "argos-es-en@1.0");

        Assert.Equal("Hola", result.OriginalText);
        Assert.Equal("Hello", result.TranslatedText);
        Assert.Equal("es", result.SourceLanguage);
        Assert.Equal("en", result.TargetLanguage);
        Assert.Equal("argos-es-en@1.0", result.ModelIdentifier);
    }

    [Fact]
    public void TranslationResult_AllowsEmptyTranslatedText()
    {
        // A model may legitimately produce an empty translation (e.g. for punctuation-only input).
        Assert.Equal(string.Empty, new TranslationResult("…", string.Empty, "es", "en", "m").TranslatedText);
    }

    [Fact]
    public void TranslationResult_RequiresConcreteSourceLanguage()
    {
        Assert.Throws<ArgumentException>(() => new TranslationResult("Hola", "Hello", null!, "en", "m"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TranslationResult_RequiresModelIdentifier(string model)
    {
        Assert.Throws<ArgumentException>(() => new TranslationResult("Hola", "Hello", "es", "en", model));
    }

    [Fact]
    public void TranslationResult_RejectsNullTranslatedText()
    {
        Assert.Throws<ArgumentNullException>(() => new TranslationResult("Hola", null!, "es", "en", "m"));
    }
}
