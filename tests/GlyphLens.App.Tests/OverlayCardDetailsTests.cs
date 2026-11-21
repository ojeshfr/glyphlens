using GlyphLens.App.ViewModels;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.Tests;

/// <summary>The one-line details under a translation in the overlay: language, time and, only when reported, the OCR confidence.</summary>
public sealed class OverlayCardDetailsTests
{
    private static RegionTranslation Result(double? confidence) => new()
    {
        Region = new ScreenTextRegion(Guid.NewGuid(), "你好", 0, 0, 40, 20, DetectionSource.Ocr, confidence, "zh", DateTimeOffset.UnixEpoch),
        Status = RegionTranslationStatus.Translated,
        TranslatedText = "Hello",
        SourceLanguage = "zh",
    };

    [Fact]
    public void WithAConfidence_TheDetailsNameLanguageTimeAndConfidence()
    {
        var card = new OverlayCard(Result(0.97), DateTimeOffset.UnixEpoch);

        Assert.Equal($"{card.Language} · {card.TimeText} · OCR 97%", card.Details);
    }

    [Fact]
    public void WithoutAConfidence_NoneIsInvented()
    {
        var card = new OverlayCard(Result(null), DateTimeOffset.UnixEpoch);

        Assert.Equal($"{card.Language} · {card.TimeText}", card.Details);
        Assert.DoesNotContain("OCR", card.Details, StringComparison.Ordinal);
    }
}
