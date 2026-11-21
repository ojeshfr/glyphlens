using GlyphLens.App.ViewModels;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.Tests;

public sealed class OverlayViewModelTests
{
    private sealed record Rig(
        OverlayViewModel Overlay,
        TranslationViewModel Translation,
        FakeRecognisedSource Source,
        FakeLanguageDetector Detector,
        FakeTranslationEngine Engine,
        FakeModelCatalog Catalog,
        List<string> Copied);

    private static Rig Create(Action<string>? copy = null)
    {
        var source = new FakeRecognisedSource();
        var detector = new FakeLanguageDetector();
        var engine = new FakeTranslationEngine();
        var catalog = new FakeModelCatalog();
        catalog.Add("zh", "en");
        var worker = new FakeWorkerManager();
        var copied = new List<string>();
        var pipeline = new TranslationPipeline(detector, catalog, engine, new LruTranslationCache());
        var translation = new TranslationViewModel(pipeline, catalog, worker, source, null, null, uiContext: null);
        worker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        translation.IsEnabled = true;
        var overlay = new OverlayViewModel(translation, copy ?? copied.Add);
        return new Rig(overlay, translation, source, detector, engine, catalog, copied);
    }

    private static async Task ReadAsync(Rig rig, params ScreenTextRegion[] regions)
    {
        rig.Source.Raise(regions);
        await rig.Translation.WaitForIdleAsync();
    }

    private static ScreenTextRegion Zh(string text, int x = 20, int y = 20, int width = 200, int height = 24, double? confidence = null) =>
        new(Guid.NewGuid(), text, x, y, width, height, DetectionSource.Ocr, confidence, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task EachTranslationBecomesACard_WithOriginalTranslationLanguageAndTime()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);

        await ReadAsync(rig, Zh("你好世界"));

        var card = Assert.Single(rig.Overlay.Cards);
        Assert.Equal("你好世界", card.Original);
        Assert.Equal("EN:你好世界", card.Translated);
        Assert.True(card.HasTranslation);
        Assert.Equal("Chinese", card.Language);
        Assert.False(card.HasNote);
        Assert.False(card.IsProblem);
        Assert.Matches(@"^\d\d:\d\d:\d\d$", card.TimeText);
        Assert.False(rig.Overlay.HasNoCards);
    }

    [Fact]
    public async Task TheRecognisersConfidenceIsShownOnlyWhenItWasReported()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        rig.Detector.Set("谢谢你的帮助", "zh", null);

        await ReadAsync(rig, Zh("你好世界", confidence: 0.987), Zh("谢谢你的帮助", y: 80));

        Assert.True(rig.Overlay.Cards[0].HasConfidence);
        Assert.Equal("OCR 99%", rig.Overlay.Cards[0].Confidence);
        Assert.False(rig.Overlay.Cards[1].HasConfidence);
        Assert.Equal(string.Empty, rig.Overlay.Cards[1].Confidence);
    }

    [Fact]
    public async Task TheNewestCardIsLast()
    {
        var rig = Create();
        rig.Detector.Set("第一条消息", "zh", null);
        rig.Detector.Set("第二条消息", "zh", null);

        await ReadAsync(rig, Zh("第一条消息"));
        await ReadAsync(rig, Zh("第一条消息"), Zh("第二条消息", y: 80));

        Assert.Equal(["第一条消息", "第二条消息"], rig.Overlay.Cards.Select(c => c.Original));
    }

    [Fact]
    public async Task TheSameTextComingBack_UpdatesItsCardInPlace_InsteadOfAddingOne()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));
        var card = rig.Overlay.Cards[0];
        var before = card.UpdatedAt;
        await Task.Delay(5);

        await ReadAsync(rig, Zh("你好世界"));

        Assert.Same(card, Assert.Single(rig.Overlay.Cards));
        Assert.True(card.UpdatedAt >= before);
    }

    [Fact]
    public async Task TextThatMovedOnScreen_IsStillTheSameCard()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界", y: 400));

        await ReadAsync(rig, Zh("你好世界", y: 20)); // the chat scrolled

        Assert.Single(rig.Overlay.Cards);
    }

    [Fact]
    public async Task ChangedTextInTheSameScreenRegion_UpdatesTheRelevantCard()
    {
        var rig = Create();
        rig.Detector.Set("现在三点", "zh", null);
        rig.Detector.Set("现在四点", "zh", null);
        await ReadAsync(rig, Zh("现在三点", x: 20, y: 20, width: 200, height: 24));
        var card = rig.Overlay.Cards[0];

        await ReadAsync(rig, Zh("现在四点", x: 22, y: 21, width: 200, height: 24));

        Assert.Same(card, Assert.Single(rig.Overlay.Cards));
        Assert.Equal("现在四点", card.Original);
        Assert.Equal("EN:现在四点", card.Translated);
    }

    [Fact]
    public async Task UpdatingACard_RaisesChangesOnlyForWhatChanged()
    {
        var rig = Create();
        rig.Detector.Set("现在三点", "zh", null);
        rig.Detector.Set("现在四点", "zh", null);
        await ReadAsync(rig, Zh("现在三点"));
        var changed = new List<string?>();
        rig.Overlay.Cards[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await ReadAsync(rig, Zh("现在三点")); // identical text: only the time may change

        Assert.DoesNotContain(nameof(OverlayCard.Original), changed);
        Assert.DoesNotContain(nameof(OverlayCard.Translated), changed);
    }

    [Fact]
    public async Task TheCardListIsBounded_AndTheOldestCardsAreDropped()
    {
        var rig = Create();
        for (int i = 0; i < OverlayViewModel.MaxCards + 50; i++)
        {
            string text = $"消息{i}号";
            rig.Detector.Set(text, "zh", null);
            await ReadAsync(rig, Zh(text, y: i * 100)); // distinct regions, so each is its own card
        }

        Assert.Equal(OverlayViewModel.MaxCards, rig.Overlay.Cards.Count);
        Assert.Equal($"消息{OverlayViewModel.MaxCards + 49}号", rig.Overlay.Cards[^1].Original);
        Assert.DoesNotContain(rig.Overlay.Cards, c => c.Original == "消息0号");
    }

    [Fact]
    public async Task ClearSession_EmptiesTheCards()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));

        rig.Overlay.ClearSession();

        Assert.Empty(rig.Overlay.Cards);
        Assert.True(rig.Overlay.HasNoCards);
    }

    [Fact]
    public async Task TheClearCommand_AsksForConfirmationFirst_AndTheSecondRequestClears()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));

        await rig.Overlay.ClearSessionCommand.ExecuteAsync();
        Assert.True(rig.Overlay.IsConfirmingClear);
        Assert.Single(rig.Overlay.Cards);

        await rig.Overlay.ClearSessionCommand.ExecuteAsync();
        Assert.False(rig.Overlay.IsConfirmingClear);
        Assert.Empty(rig.Overlay.Cards);
    }

    [Fact]
    public async Task CancellingTheConfirmation_KeepsTheCards()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));
        rig.Overlay.RequestClear();

        rig.Overlay.CancelClearCommand.Execute(null);

        Assert.False(rig.Overlay.IsConfirmingClear);
        Assert.Single(rig.Overlay.Cards);
    }

    [Fact]
    public void ClearingNothing_AsksForNothing()
    {
        var rig = Create();

        rig.Overlay.RequestClear();

        Assert.False(rig.Overlay.IsConfirmingClear);
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, ManualTimer Timer)> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add((_now + dueTime, timer));
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var (due, timer) in _timers.ToList())
            {
                if (due <= _now)
                {
                    _ = _timers.Remove((due, timer));
                    timer.Fire();
                }
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public void Fire() => callback(state);
        }
    }

    [Fact]
    public async Task AnUnconfirmedClear_ExpiresAfterTheTimeout_AndKeepsTheCards()
    {
        var time = new ManualTime();
        var rig = Create();
        var overlay = new OverlayViewModel(rig.Translation, null, null, time);
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));
        overlay.RequestClear();
        Assert.True(overlay.IsConfirmingClear);

        time.Advance(OverlayViewModel.ClearConfirmationTimeout + TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.False(overlay.IsConfirmingClear);
        Assert.Single(overlay.Cards);
        overlay.Dispose();
    }

    [Fact]
    public void TheFontSize_IsClampedAndChangedByItsCommands()
    {
        var rig = Create();
        Assert.Equal(OverlayViewModel.DefaultFontSize, rig.Overlay.FontSize);
        Assert.Equal(OverlayViewModel.DefaultFontSize + 1, rig.Overlay.TranslationFontSize);

        rig.Overlay.IncreaseFontCommand.Execute(null);
        Assert.Equal(14, rig.Overlay.FontSize);

        rig.Overlay.FontSize = 500;
        Assert.Equal(OverlayViewModel.MaxFontSize, rig.Overlay.FontSize);
        Assert.False(rig.Overlay.IncreaseFontCommand.CanExecute(null));

        rig.Overlay.FontSize = -3;
        Assert.Equal(OverlayViewModel.MinFontSize, rig.Overlay.FontSize);
        Assert.False(rig.Overlay.DecreaseFontCommand.CanExecute(null));

        rig.Overlay.FontSize = double.NaN;
        Assert.Equal(OverlayViewModel.DefaultFontSize, rig.Overlay.FontSize);
    }

    [Fact]
    public async Task Clearing_AlsoForgetsThatNewResultsWereWaiting()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));
        rig.Overlay.HasUnseenResults = true;

        rig.Overlay.ClearSession();

        Assert.False(rig.Overlay.HasUnseenResults);
    }

    [Fact]
    public async Task ACardWithoutAModel_ExplainsWhy_AndInventsNoTranslation()
    {
        var rig = Create();
        rig.Detector.Set("こんにちは世界", "ja", null);

        await ReadAsync(rig, Zh("こんにちは世界"));

        var card = Assert.Single(rig.Overlay.Cards);
        Assert.False(card.HasTranslation);
        Assert.Null(card.Translated);
        Assert.True(card.IsProblem);
        Assert.Contains("translate-ja_en", card.Note, StringComparison.Ordinal);
        Assert.False(rig.Overlay.CopyTranslationCommand.CanExecute(card));
        Assert.True(rig.Overlay.CopyOriginalCommand.CanExecute(card));
    }

    [Fact]
    public async Task CopyCommands_CopyTheCardsOriginalOrTranslation_Separately()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));
        var card = rig.Overlay.Cards[0];

        rig.Overlay.CopyOriginalCommand.Execute(card);
        rig.Overlay.CopyTranslationCommand.Execute(card);

        Assert.Equal(["你好世界", "EN:你好世界"], rig.Copied);
        Assert.Equal("Copied the translation.", rig.Overlay.StatusMessage);
    }

    [Fact]
    public async Task ABusyClipboard_IsReported_NotThrown()
    {
        var rig = Create(_ => throw new System.Runtime.InteropServices.ExternalException("busy"));
        rig.Detector.Set("你好世界", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));

        rig.Overlay.CopyOriginalCommand.Execute(rig.Overlay.Cards[0]);

        Assert.Equal("The clipboard is busy; try again.", rig.Overlay.StatusMessage);
    }

    [Fact]
    public void Defaults_AreInteractiveAndPinnedAndExpanded()
    {
        var rig = Create();

        Assert.True(rig.Overlay.IsInteractive);
        Assert.False(rig.Overlay.IsPassThrough);
        Assert.Equal("Interactive", rig.Overlay.ModeText);
        Assert.True(rig.Overlay.IsPinned);
        Assert.False(rig.Overlay.IsCollapsed);
        Assert.True(rig.Overlay.IsExpanded);
        Assert.False(rig.Overlay.IsVisible);
        Assert.Equal(1.0, rig.Overlay.Opacity);
    }

    [Fact]
    public void TheInputModeToggles_AndSaysWhichModeItIs()
    {
        var rig = Create();
        var changed = new List<string?>();
        rig.Overlay.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Overlay.ToggleInteractive();

        Assert.True(rig.Overlay.IsPassThrough);
        Assert.Equal("Pass-through", rig.Overlay.ModeText);
        Assert.Contains(nameof(OverlayViewModel.ModeText), changed);
        rig.Overlay.ToggleInteractiveCommand.Execute(null);
        Assert.True(rig.Overlay.IsInteractive);
    }

    [Theory]
    [InlineData(0.0, 0.35)]
    [InlineData(-3.0, 0.35)]
    [InlineData(0.5, 0.5)]
    [InlineData(7.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    public void OpacityIsKeptInARangeWhereTheOverlayCanStillBeSeen(double value, double expected)
    {
        var rig = Create();

        rig.Overlay.Opacity = value;

        Assert.Equal(expected, rig.Overlay.Opacity);
    }

    [Fact]
    public void CollapseHideAndShow_WorkAndShowExpandsAgain()
    {
        var rig = Create();

        rig.Overlay.ToggleCollapsed();
        Assert.True(rig.Overlay.IsCollapsed);
        Assert.False(rig.Overlay.IsExpanded);
        rig.Overlay.Show();
        Assert.True(rig.Overlay.IsVisible);
        Assert.False(rig.Overlay.IsCollapsed);
        rig.Overlay.Hide();
        Assert.False(rig.Overlay.IsVisible);
        rig.Overlay.ToggleVisible();
        Assert.True(rig.Overlay.IsVisible);
        rig.Overlay.TogglePinned();
        Assert.False(rig.Overlay.IsPinned);
    }

    [Fact]
    public void TheHeaderReflectsTheTranslationLanguagesAndActivity()
    {
        var rig = Create();

        Assert.Equal("Auto", rig.Overlay.SourceText);
        Assert.Equal("English", rig.Overlay.TargetText);
        Assert.Equal("Active", rig.Overlay.ActivityText);

        rig.Overlay.TogglePause();
        Assert.Equal("Paused", rig.Overlay.ActivityText);
        Assert.False(rig.Overlay.IsTranslationActive);
        rig.Overlay.TogglePause();
        rig.Overlay.ToggleTranslation();
        Assert.Equal("Off", rig.Overlay.ActivityText);
    }

    [Fact]
    public async Task ChoosingTheSourceLanguage_IsShownInTheHeader_TheChineseScriptsByTheirOwnNames()
    {
        var rig = Create();
        rig.Catalog.Add("zt", "en");
        await rig.Translation.RefreshModelsCommand.ExecuteAsync();
        await Task.Delay(50);
        Assert.Equal("Auto", rig.Overlay.SourceText);

        rig.Translation.SelectedSource = rig.Translation.SourceOptions.First(o => o.Code == "zh");
        Assert.Equal("\u4e2d\u6587", rig.Overlay.SourceText);

        rig.Translation.SelectedSource = rig.Translation.SourceOptions.First(o => o.Code == "zt");
        Assert.Equal("\u7e41\u9ad4\u4e2d\u6587", rig.Overlay.SourceText);

        var other = rig.Translation.SourceOptions.First(o => o.Code == "ja");
        rig.Translation.SelectedSource = other;
        Assert.Equal(other.Label, rig.Overlay.SourceText);
    }

    [Fact]
    public async Task WhilePaused_NewRecognitionIsIgnored_AndResumingTranslatesTheLatest()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);
        rig.Detector.Set("再见朋友", "zh", null);
        await ReadAsync(rig, Zh("你好世界"));

        rig.Overlay.TogglePause();
        rig.Source.Raise(Zh("再见朋友"));
        await rig.Translation.WaitForIdleAsync();
        Assert.Single(rig.Overlay.Cards); // nothing new while paused

        rig.Overlay.TogglePause();
        await rig.Translation.WaitForIdleAsync();
        // Resuming translates what was recognised last (the paused pass was ignored, so nothing new arrived).
        Assert.Equal("你好世界", rig.Overlay.Cards[0].Original);
    }

    [Fact]
    public void SettingsShortcut_RaisesTheRequest()
    {
        var rig = Create();
        int requests = 0;
        rig.Overlay.SettingsRequested += (_, _) => requests++;

        rig.Overlay.SettingsCommand.Execute(null);

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task AfterDispose_NoMoreCardsAreAdded()
    {
        var rig = Create();
        rig.Detector.Set("你好世界", "zh", null);

        rig.Overlay.Dispose();
        rig.Overlay.Dispose();
        await ReadAsync(rig, Zh("你好世界"));

        Assert.Empty(rig.Overlay.Cards);
    }

    [Fact]
    public async Task NoticesFromTheTranslationAreVisibleInTheOverlay()
    {
        var rig = Create();
        rig.Detector.Set("こんにちは世界", "ja", null);

        await ReadAsync(rig, Zh("こんにちは世界"));

        Assert.True(rig.Overlay.HasNotices);
        Assert.Contains("translate-ja_en", Assert.Single(rig.Overlay.Notices).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlap_IsIntersectionOverUnion()
    {
        Assert.Equal(1.0, OverlayViewModel.Overlap((0, 0, 10, 10), (0, 0, 10, 10)));
        Assert.Equal(0.0, OverlayViewModel.Overlap((0, 0, 10, 10), (20, 20, 10, 10)));
        Assert.Equal(50.0 / 150.0, OverlayViewModel.Overlap((0, 0, 10, 10), (5, 0, 10, 10)), 6);
        Assert.Equal(0.0, OverlayViewModel.Overlap((0, 0, 10, 10), (10, 0, 10, 10))); // touching is not overlapping
    }

    [Fact]
    public void Constructor_RejectsNull() => Assert.Throws<ArgumentNullException>(() => new OverlayViewModel(null!));
}
