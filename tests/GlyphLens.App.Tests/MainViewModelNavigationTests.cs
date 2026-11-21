using System.ComponentModel;
using GlyphLens.App.ViewModels;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.Tests;

public sealed class MainViewModelNavigationTests
{
    private static MainViewModel Create()
    {
        var captureService = new FakeCaptureService();
        var capture = new CaptureViewModel(captureService, new FakeTargetProvider(), null);
        var ocrEngine = new FakeOcr();
        var ocr = new OcrViewModel(ocrEngine, ocrEngine, captureService, capture.Overlay, null, null, TimeSpan.FromMilliseconds(10));
        var catalog = new FakeModelCatalog();
        var worker = new FakeWorkerManager();
        var pipeline = new TranslationPipeline(new FakeLanguageDetector(), catalog, new FakeTranslationEngine(), new LruTranslationCache());
        var translation = new TranslationViewModel(pipeline, catalog, worker, ocr, new FakeModelFilePicker(), _ => { }, uiContext: null);
        return new MainViewModel(capture, ocr, translation, new OverlayViewModel(translation), new WorkerViewModel(worker, null));
    }

    [Fact]
    public void ThePagesAreTheFourThatHaveRealContent_AndHistoryIsNotOneOfThem()
    {
        using var main = Create();

        Assert.Equal(["Overview", "Capture", "Models", "Settings"], main.Pages.Select(p => p.Label));
        Assert.Equal(["NavOverview", "NavCapture", "NavModels", "NavSettings"], main.Pages.Select(p => p.AutomationId));
        Assert.DoesNotContain(main.Pages, p => p.Label == "History");
    }

    [Fact]
    public void TheOverviewIsShownFirst()
    {
        using var main = Create();

        Assert.Equal(MainPage.Overview, main.SelectedPage);
        Assert.True(main.IsOverviewPage);
        Assert.False(main.IsCapturePage || main.IsModelsPage || main.IsSettingsPage);
    }

    [Theory]
    [InlineData(MainPage.Capture)]
    [InlineData(MainPage.Models)]
    [InlineData(MainPage.Settings)]
    [InlineData(MainPage.Overview)]
    public void SelectingAPage_ShowsExactlyThatPage_AndNotifies(MainPage page)
    {
        using var main = Create();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)main).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        main.Navigate(MainPage.Capture);
        changed.Clear();

        main.Navigate(page);

        Assert.Equal(page, main.SelectedPage);
        Assert.Equal(1, new[] { main.IsOverviewPage, main.IsCapturePage, main.IsModelsPage, main.IsSettingsPage }.Count(v => v));
        if (page != MainPage.Capture)
        {
            Assert.Contains(nameof(MainViewModel.SelectedPage), changed);
        }
    }

    [Fact]
    public void EveryPageHasItsOwnIcon()
    {
        using var main = Create();

        Assert.All(main.Pages, p => Assert.False(string.IsNullOrEmpty(p.Glyph)));
        Assert.Equal(main.Pages.Count, main.Pages.Select(p => p.Glyph).Distinct().Count());
    }

    [Fact]
    public void ANavigationItemIsNamedByItsLabel() =>
        Assert.Equal("Models", new NavItem(MainPage.Models, "Models", "NavModels").ToString());

    [Fact]
    public void SettingTheSelectionToNothing_IsIgnored()
    {
        using var main = Create();
        main.Navigate(MainPage.Settings);

        main.SelectedNavItem = null!;

        Assert.Equal(MainPage.Settings, main.SelectedPage);
    }

    [Fact]
    public void ThePrivacyText_NamesTheUpdateCheckAsTheOnlyNetworkUse_AndNoPersistence_AndDoesNotOverclaim()
    {
        using var main = Create();

        Assert.Contains("only network requests are update checks", main.PrivacyText, StringComparison.Ordinal);
        Assert.Contains("never leaves this computer", main.PrivacyText, StringComparison.Ordinal);
        Assert.DoesNotContain("no network requests", main.PrivacyText, StringComparison.Ordinal);
        Assert.Contains("never written to disk", main.PrivacyText, StringComparison.Ordinal);
        Assert.DoesNotContain("100%", main.PrivacyText, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented yet", main.FeatureStatus, StringComparison.Ordinal);
    }
}
