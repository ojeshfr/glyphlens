using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.Tests;

public sealed class OcrEngineAdvisorTests
{
    private const string LocalTag = OcrEngineSelector.LocalEngineTag;

    private sealed record Rig(
        OcrEngineAdvisor Advisor,
        OcrViewModel Ocr,
        TranslationViewModel Translation,
        FakeWorkerManager Worker,
        FakeModelCatalog Catalog,
        FakeOcr Engine) : IDisposable
    {
        public void Dispose()
        {
            Advisor.Dispose();
            Translation.Dispose();
            Ocr.Dispose();
        }
    }

    private static Rig Create(bool windowsReadsChinese = false, bool chineseModel = true)
    {
        var engine = new FakeOcr
        {
            AvailableLanguages = windowsReadsChinese
                ? [new("en-US", "English (United States)"), new("zh-Hans-CN", "Chinese (Simplified)"), new(LocalTag, "Chinese + English (local OCR worker)")]
                : [new("en-US", "English (United States)"), new(LocalTag, "Chinese + English (local OCR worker)")],
        };
        var ocr = new OcrViewModel(engine, engine, new FakeCaptureService(), new OverlayState(), null, null, TimeSpan.FromMilliseconds(10));

        var catalog = new FakeModelCatalog();
        if (chineseModel)
        {
            catalog.Add("zh", "en");
        }

        var worker = new FakeWorkerManager();
        var pipeline = new TranslationPipeline(new FakeLanguageDetector(), catalog, new FakeTranslationEngine(), new LruTranslationCache());
        var translation = new TranslationViewModel(pipeline, catalog, worker, ocr, new FakeModelFilePicker(), _ => { }, uiContext: null);
        var advisor = new OcrEngineAdvisor(ocr, translation, worker, uiContext: null);
        return new Rig(advisor, ocr, translation, worker, catalog, engine);
    }

    private static async Task RefreshModelsAsync(Rig rig)
    {
        await rig.Translation.RefreshModelsCommand.ExecuteAsync();
    }

    [Fact]
    public void WithoutTheWorker_ChineseAsTheSourceDoesNotSwitchAndExplainsTheLimit()
    {
        using var rig = Create(chineseModel: false);
        Assert.False(rig.Ocr.HasEngineNotice);

        rig.Translation.SelectedSource = rig.Translation.SourceOptions.First(o => o.Code == "zh");

        Assert.Equal("Automatic (your Windows languages)", rig.Ocr.SelectedLanguage.Label);
        Assert.True(rig.Ocr.HasEngineNotice);
        Assert.Contains("Turn translation on", rig.Ocr.EngineNotice, StringComparison.Ordinal);
        Assert.Contains(rig.Translation.Notices, n => n.Text.Contains("Turn translation on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheLocalOcrIsReady_ChineseSelectsItAutomatically_WithoutCountingAsTheUsersChoice()
    {
        using var rig = Create();
        await RefreshModelsAsync(rig);

        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.Ready);

        Assert.Equal(LocalTag, rig.Ocr.SelectedLanguage.Tag);
        Assert.False(rig.Ocr.UserChoseLanguage);
        Assert.Equal(LocalTag, rig.Engine.SelectedLanguage);
        Assert.Contains("local Chinese OCR", rig.Ocr.EngineNotice, StringComparison.Ordinal);
        Assert.False(rig.Ocr.EngineNoticeIsProblem);
    }

    [Fact]
    public async Task AnExplicitChoiceOfAnEnglishOnlyEngine_IsKept_AndExplained()
    {
        using var rig = Create();
        await RefreshModelsAsync(rig);
        rig.Ocr.SelectedLanguage = rig.Ocr.Languages.First(o => o.Tag == "en-US");

        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.Ready);

        Assert.Equal("en-US", rig.Ocr.SelectedLanguage.Tag);
        Assert.True(rig.Ocr.UserChoseLanguage);
        Assert.True(rig.Ocr.EngineNoticeIsProblem);
        Assert.Contains("cannot read Chinese", rig.Ocr.EngineNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingModels_NeverSwitch_AndOfferTheFolder()
    {
        using var rig = Create();
        await RefreshModelsAsync(rig);

        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.NotInstalled);

        Assert.Equal("Automatic (your Windows languages)", rig.Ocr.SelectedLanguage.Label);
        Assert.True(rig.Ocr.EngineNoticeIsProblem);
        Assert.True(rig.Ocr.OffersOcrFolder);
        Assert.Contains("models are not installed", rig.Ocr.EngineNotice, StringComparison.Ordinal);
        Assert.Contains(rig.Translation.Notices, n => n.IsError && n.Text.Contains("models are not installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingRuntime_IsExplained_WithoutTheFolderOffer()
    {
        using var rig = Create();
        await RefreshModelsAsync(rig);

        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.Unavailable);

        Assert.True(rig.Ocr.EngineNoticeIsProblem);
        Assert.False(rig.Ocr.OffersOcrFolder);
        Assert.Contains("OCR packages", rig.Ocr.EngineNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAChineseModelOrChineseSource_NothingIsSaid()
    {
        using var rig = Create(chineseModel: false);

        await RefreshModelsAsync(rig);
        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.Ready);

        Assert.False(rig.Ocr.HasEngineNotice);
        Assert.Equal("Automatic (your Windows languages)", rig.Ocr.SelectedLanguage.Label);
        Assert.Empty(rig.Translation.Notices);
    }

    [Fact]
    public async Task ChoosingChineseAsTheSource_TriggersTheSwitch_EvenWithoutAnInstalledModel()
    {
        using var rig = Create(chineseModel: false);
        await RefreshModelsAsync(rig);
        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.Ready);
        Assert.False(rig.Ocr.HasEngineNotice);

        rig.Translation.SelectedSource = rig.Translation.SourceOptions.First(o => o.Code == "zh");

        Assert.Equal(LocalTag, rig.Ocr.SelectedLanguage.Tag);
        Assert.True(rig.Ocr.HasEngineNotice);
    }

    [Fact]
    public async Task WhenWindowsCanReadChinese_NoteIsOnlyInformationAboutMissingLocalModels()
    {
        using var rig = Create(windowsReadsChinese: true);
        await RefreshModelsAsync(rig);

        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.NotInstalled);

        Assert.False(rig.Ocr.EngineNoticeIsProblem);
        Assert.DoesNotContain("cannot read Chinese", rig.Ocr.EngineNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingTheWorker_ForgetsTheReadinessAgain()
    {
        using var rig = Create();
        await RefreshModelsAsync(rig);
        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.NotInstalled);
        Assert.Contains("models are not installed", rig.Ocr.EngineNotice, StringComparison.Ordinal);

        await rig.Worker.StopAsync(CancellationToken.None);

        Assert.Contains("Turn translation on", rig.Ocr.EngineNotice, StringComparison.Ordinal);
        Assert.False(rig.Ocr.OffersOcrFolder);
    }

    [Fact]
    public void LanguageOptions_AreNamedByTheirLabel_ForScreenReadersAndUiAutomation()
    {
        using var rig = Create();

        Assert.All(rig.Ocr.Languages, o => Assert.Equal(o.Label, o.ToString()));
        Assert.All(rig.Translation.SourceOptions, o => Assert.Equal(o.Label, o.ToString()));
        Assert.All(rig.Translation.TargetOptions, o => Assert.Equal(o.Label, o.ToString()));
    }

    [Fact]
    public void TheFolderCommand_RaisesTheRequest()
    {
        using var rig = Create();
        int requests = 0;
        rig.Ocr.OpenOcrFolderRequested += (_, _) => requests++;

        rig.Ocr.OpenOcrFolderCommand.Execute(null);

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ADisposedAdvisor_StopsReacting()
    {
        var rig = Create();
        await RefreshModelsAsync(rig);
        rig.Advisor.Dispose();

        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.ReportOcr(WorkerComponentState.Ready);

        Assert.Equal("Automatic (your Windows languages)", rig.Ocr.SelectedLanguage.Label);
        rig.Translation.Dispose();
        rig.Ocr.Dispose();
    }
}
