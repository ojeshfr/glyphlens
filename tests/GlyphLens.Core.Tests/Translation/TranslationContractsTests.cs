using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.Core.Tests.Translation;

public sealed class TranslationContractsTests
{
    [Theory]
    [InlineData("Hello   world", "Hello world")]
    [InlineData("  padded\t\ttext \r\n next line ", "padded text next line")]
    [InlineData("Case Matters", "Case Matters")]
    [InlineData("", "")]
    [InlineData("   \r\n ", "")]
    public void Normalize_CollapsesWhitespaceAndKeepsCase(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Normalize_ComposesAccents_SoDecomposedAndComposedTextMatch()
    {
        string composed = "caf\u00E9";
        string decomposed = "cafe\u0301";

        Assert.NotEqual(composed, decomposed);
        Assert.Equal(TextNormalizer.Normalize(composed), TextNormalizer.Normalize(decomposed));
    }

    [Fact]
    public void Normalize_OfNull_IsEmpty() => Assert.Equal(string.Empty, TextNormalizer.Normalize(null));

    [Theory]
    [InlineData("好", true)]
    [InlineData("我们", true)]
    [InlineData("hello 世界", true)]
    [InlineData("こんにちは", true)]
    [InlineData("안녕", true)]
    [InlineData("𠀀", true)]
    [InlineData("Hallo", false)]
    [InlineData("12:30", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsCjk_RecognisesHanKanaAndHangul(string? text, bool expected) =>
        Assert.Equal(expected, TextNormalizer.ContainsCjk(text));

    [Theory]
    [InlineData("Hallo", 5)]
    [InlineData("12:30", 0)]
    [InlineData("a1b2", 2)]
    [InlineData("こんにちは", 5)]
    [InlineData("", 0)]
    public void CountLetters_CountsOnlyLetters(string text, int expected) =>
        Assert.Equal(expected, TextNormalizer.CountLetters(text));

    [Theory]
    [InlineData("de", "German")]
    [InlineData("ja", "Japanese")]
    [InlineData("zt", "Chinese (Traditional)")]
    [InlineData("qq", "qq")]
    public void LanguageNames_GivesEnglishNames(string code, string expected) =>
        Assert.Equal(expected, LanguageNames.Display(code));

    [Fact]
    public void LanguageNames_HandlesMissingCodes() => Assert.Equal("unknown", LanguageNames.Display(null));

    [Theory]
    [InlineData("en", "en-US", true)]
    [InlineData("zh-Hant", "zh", true)]
    [InlineData("de", "en", false)]
    public void SamePrimary_ComparesTheLanguageSubtag(string a, string b, bool expected) =>
        Assert.Equal(expected, LanguageNames.SamePrimary(a, b));

    [Fact]
    public void Detected_RequiresAValidCodeAndConfidence()
    {
        var result = LanguageDetectionResult.Detected("de", 0.9, [new LanguageCandidate("nl", 0.05)]);

        Assert.Equal("de", result.Language);
        Assert.Equal(0.9, result.Confidence);
        Assert.Equal(LanguageDetectionIssue.None, result.Issue);
        Assert.Single(result.Alternatives);
        Assert.Throws<ArgumentException>(() => LanguageDetectionResult.Detected("DE!", 0.9));
        Assert.Throws<ArgumentOutOfRangeException>(() => LanguageDetectionResult.Detected("de", 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LanguageDetectionResult.Detected("de", double.NaN));
    }

    [Fact]
    public void Detected_MayCarryNoConfidence() => Assert.Null(LanguageDetectionResult.Detected("de", null).Confidence);

    [Fact]
    public void Undetermined_HasNoLanguageAndNeedsAReason()
    {
        var result = LanguageDetectionResult.Undetermined(LanguageDetectionIssue.TooShort);

        Assert.Null(result.Language);
        Assert.Null(result.Confidence);
        Assert.Equal(LanguageDetectionIssue.TooShort, result.Issue);
        Assert.Throws<ArgumentException>(() => LanguageDetectionResult.Undetermined(LanguageDetectionIssue.None));
    }

    [Fact]
    public void LanguageCandidate_ValidatesItsInputs()
    {
        Assert.Throws<ArgumentException>(() => new LanguageCandidate("", 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LanguageCandidate("de", -0.1));
    }

    [Fact]
    public void TranslationOutcome_IsExactlyOneOfResultAndError()
    {
        var ok = TranslationOutcome.Success(new TranslationResult("Hallo", "Hello", "de", "en", "m"));
        var failed = TranslationOutcome.Failure(new TranslationError(TranslationErrorKind.ModelMissing, "missing", "translate-de_en"));

        Assert.True(ok.Succeeded);
        Assert.Null(ok.Error);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Result);
        Assert.Equal("translate-de_en", failed.Error!.RequiredPackage);
        Assert.Throws<ArgumentNullException>(() => TranslationOutcome.Success(null!));
        Assert.Throws<ArgumentNullException>(() => TranslationOutcome.Failure(null!));
    }

    [Fact]
    public void TranslationException_CarriesTheError()
    {
        var error = new TranslationError(TranslationErrorKind.WorkerUnavailable, "worker down");

        var exception = new TranslationException(error);

        Assert.Same(error, exception.Error);
        Assert.Equal("worker down", exception.Message);
        Assert.Equal(TranslationErrorKind.Failed, new TranslationException("boom").Error.Kind);
    }

    [Fact]
    public void RouteStatus_AvailableRouteNeedsAModelVersion()
    {
        Assert.Throws<ArgumentException>(() => new RouteStatus("de", "en", true, ["de", "en"], null, null, RouteIssue.None));

        var ok = new RouteStatus("de", "en", true, ["de", "en"], "argos:de-en@1.9", null, RouteIssue.ModelMissing);
        Assert.Equal(RouteIssue.None, ok.Issue);

        var missing = new RouteStatus("ja", "en", false, null, null, "translate-ja_en", RouteIssue.None);
        Assert.Equal(RouteIssue.ModelMissing, missing.Issue);
        Assert.Empty(missing.Route);
    }

    [Fact]
    public void ModelCatalog_ReportsInstalledSourcesAndTargets()
    {
        var catalog = new ModelCatalog(
            "argos",
            @"C:\models",
            [
                new TranslationPackageInfo("de", "en", "German", "English", "1.9", "translate-de_en"),
                new TranslationPackageInfo("fr", "en", "French", "English", "1.9", "translate-fr_en"),
                new TranslationPackageInfo("en", "es", "English", "Spanish", "1.0", "translate-en_es"),
            ],
            []);

        Assert.Equal(["de", "fr", "en"], catalog.InstalledSources);
        Assert.True(catalog.HasTarget("en"));
        Assert.True(catalog.HasTarget("ES"));
        Assert.False(catalog.HasTarget("ja"));
    }

    [Fact]
    public void WorkerComponentState_RoundTripsTheInstalledValue()
    {
        Assert.Equal("installed", WorkerComponentState.Installed.ToWireString());
        Assert.Equal(WorkerComponentState.Installed, WorkerComponentStateExtensions.ParseWire("installed"));
        Assert.Equal("INSTALLED", WorkerComponentState.Installed.ToDisplayString());
    }
}
