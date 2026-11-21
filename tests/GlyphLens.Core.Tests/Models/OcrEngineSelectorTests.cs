using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public sealed class OcrEngineSelectorTests
{
    private static readonly CapturedFrame Frame = new(
        new FrameMetadata("display:1", 4, 4, 16, PixelFormat.Bgra8, DateTimeOffset.UnixEpoch, 1),
        new byte[64]);

    private sealed class FakeEngine(string name) : IOcrEngine
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
        {
            Calls++;
            IReadOnlyList<ScreenTextRegion> result = [new(Guid.NewGuid(), name, 0, 0, 1, 1, DetectionSource.Ocr, null, null, DateTimeOffset.UnixEpoch)];
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLanguages : IOcrLanguageSource
    {
        public IReadOnlyList<OcrLanguage> AvailableLanguages { get; init; } = [new("en-US", "English (United States)")];

        public string? SelectedLanguage { get; set; }
    }

    [Fact]
    public void AvailableLanguages_ListTheWindowsLanguagesAndTheLocalEngine()
    {
        var selector = new OcrEngineSelector(new FakeEngine("windows"), new FakeLanguages(), new FakeEngine("local"), "Chinese and English (local)");

        Assert.Equal(["en-US", OcrEngineSelector.LocalEngineTag], selector.AvailableLanguages.Select(l => l.Tag));
        Assert.Equal("Chinese and English (local)", selector.AvailableLanguages[^1].DisplayName);
    }

    [Fact]
    public void AMachineWithoutWindowsOcrLanguages_StillOffersTheLocalEngine()
    {
        var selector = new OcrEngineSelector(new FakeEngine("windows"), new FakeLanguages { AvailableLanguages = [] }, new FakeEngine("local"), "Local");

        Assert.Equal([OcrEngineSelector.LocalEngineTag], selector.AvailableLanguages.Select(l => l.Tag));
    }

    [Fact]
    public async Task ByDefaultRecognitionGoesToWindowsOcr()
    {
        var windows = new FakeEngine("windows");
        var local = new FakeEngine("local");
        var selector = new OcrEngineSelector(windows, new FakeLanguages(), local, "Local");

        var regions = await selector.RecognizeAsync(Frame, CancellationToken.None);

        Assert.Equal("windows", Assert.Single(regions).Text);
        Assert.False(selector.IsLocal);
        Assert.Equal((1, 0), (windows.Calls, local.Calls));
    }

    [Fact]
    public async Task PickingTheLocalEngineSendsRecognitionThere_AndPickingAWindowsLanguageGoesBack()
    {
        var windows = new FakeEngine("windows");
        var local = new FakeEngine("local");
        var languages = new FakeLanguages();
        var selector = new OcrEngineSelector(windows, languages, local, "Local");

        selector.SelectedLanguage = OcrEngineSelector.LocalEngineTag;
        var first = await selector.RecognizeAsync(Frame, CancellationToken.None);
        selector.SelectedLanguage = "en-US";
        var second = await selector.RecognizeAsync(Frame, CancellationToken.None);

        Assert.Equal("local", Assert.Single(first).Text);
        Assert.Equal("windows", Assert.Single(second).Text);
        Assert.Equal("en-US", languages.SelectedLanguage);
        Assert.Equal((1, 1), (windows.Calls, local.Calls));
    }

    [Fact]
    public void ThePickedLocalEngineIsNotPassedToWindowsOcr()
    {
        var languages = new FakeLanguages { SelectedLanguage = "en-US" };
        var selector = new OcrEngineSelector(new FakeEngine("windows"), languages, new FakeEngine("local"), "Local");

        selector.SelectedLanguage = OcrEngineSelector.LocalEngineTag;

        Assert.True(selector.IsLocal);
        Assert.Equal("en-US", languages.SelectedLanguage); // Windows OCR keeps its own last language
    }

    [Fact]
    public void ItStartsWithTheLanguageWindowsOcrAlreadyHas()
    {
        var selector = new OcrEngineSelector(new FakeEngine("windows"), new FakeLanguages { SelectedLanguage = "en-US" }, new FakeEngine("local"), "Local");

        Assert.Equal("en-US", selector.SelectedLanguage);
    }

    [Fact]
    public void ConstructorRejectsMissingParts()
    {
        var engine = new FakeEngine("x");
        var languages = new FakeLanguages();

        Assert.Throws<ArgumentNullException>(() => new OcrEngineSelector(null!, languages, engine, "x"));
        Assert.Throws<ArgumentNullException>(() => new OcrEngineSelector(engine, null!, engine, "x"));
        Assert.Throws<ArgumentNullException>(() => new OcrEngineSelector(engine, languages, null!, "x"));
        Assert.Throws<ArgumentException>(() => new OcrEngineSelector(engine, languages, engine, " "));
    }
}
