using GlyphLens.App.ViewModels;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.Tests;

public sealed class TranslationViewModelTests
{
    private sealed record Rig(
        TranslationViewModel Vm,
        FakeRecognisedSource Source,
        FakeLanguageDetector Detector,
        FakeTranslationEngine Engine,
        FakeModelCatalog Catalog,
        FakeWorkerManager Worker,
        FakeModelFilePicker Picker,
        List<string> Copied,
        TranslationPipeline Pipeline);

    private static Rig Create(bool workerReady = true, Action<FakeModelCatalog>? catalog = null)
    {
        var source = new FakeRecognisedSource();
        var detector = new FakeLanguageDetector();
        var engine = new FakeTranslationEngine();
        var models = new FakeModelCatalog();
        models.Add("de", "en");
        catalog?.Invoke(models);
        var worker = new FakeWorkerManager();
        var picker = new FakeModelFilePicker();
        var copied = new List<string>();
        var pipeline = new TranslationPipeline(detector, models, engine, new LruTranslationCache());
        var vm = new TranslationViewModel(pipeline, models, worker, source, picker, copied.Add, uiContext: null);
        if (workerReady)
        {
            worker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return new Rig(vm, source, detector, engine, models, worker, picker, copied, pipeline);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private static async Task TranslateAsync(Rig rig, params string[] texts)
    {
        rig.Source.Raise([.. texts.Select(t => FakeRecognisedSource.Region(t))]);
        await rig.Vm.WaitForIdleAsync();
    }

    [Fact]
    public void InitialState_IsOffAndIdle()
    {
        var rig = Create(workerReady: false);

        Assert.False(rig.Vm.IsEnabled);
        Assert.Empty(rig.Vm.Items);
        Assert.False(rig.Vm.CanCopyOriginal);
        Assert.False(rig.Vm.CanCopyTranslation);
        Assert.False(rig.Vm.HasNotices);
        Assert.Null(rig.Vm.SelectedSource.Code);
        Assert.Equal("en", rig.Vm.SelectedTarget.Code);
        Assert.Equal(["Detect automatically"], rig.Vm.SourceOptions.Take(1).Select(o => o.Label));
        Assert.Contains("Turn on translation", rig.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Recognition_WhileTranslationIsOff_DoesNothing()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");

        rig.Source.Raise(FakeRecognisedSource.Region("Hallo Welt"));

        Assert.Empty(rig.Vm.Items);
        Assert.Equal(0, rig.Engine.Calls);
        Assert.Equal(0, rig.Detector.TextsDetected);
    }

    [Fact]
    public void TurningTranslationOn_StartsAStoppedWorker()
    {
        var rig = Create(workerReady: false);

        rig.Vm.IsEnabled = true;

        Assert.Equal(1, rig.Worker.StartCalls);
        Assert.Equal(WorkerStatus.Ready, rig.Worker.Status);
    }

    [Fact]
    public void TurningTranslationOn_LeavesARunningWorkerAlone()
    {
        var rig = Create();
        int starts = rig.Worker.StartCalls;

        rig.Vm.IsEnabled = true;

        Assert.Equal(starts, rig.Worker.StartCalls);
    }

    [Fact]
    public async Task TurningTranslationOn_WhenStartFails_ShowsTheWorkerError()
    {
        var rig = Create(workerReady: false);
        rig.Worker.FailStartWith = new WorkerException(WorkerErrorKind.PythonNotFound, "Python 3.11 or newer was not found.");

        rig.Vm.IsEnabled = true;

        Assert.True(await UntilAsync(() => rig.Vm.HasModelsError));
        Assert.Contains("Python", rig.Vm.ModelsError, StringComparison.Ordinal);
        Assert.True(rig.Vm.CanStartWorker);
    }

    [Fact]
    public async Task GermanLine_IsTranslated_AndOriginalIsKept()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Vm.IsEnabled = true;

        await TranslateAsync(rig, "Hallo Welt");

        var item = Assert.Single(rig.Vm.Items);
        Assert.Equal("Hallo Welt", item.Original);
        Assert.Equal("EN:Hallo Welt", item.Translated);
        Assert.Equal(TranslationItemKind.Translated, item.Kind);
        Assert.Contains("German", item.Language, StringComparison.Ordinal);
        Assert.Contains("95%", item.Language, StringComparison.Ordinal);
        Assert.False(rig.Vm.HasNotices);
        Assert.Contains("1 of 1 translated", rig.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyCommands_CopyOriginalAndTranslatedTextSeparately()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Detector.Set("Guten Tag", "de");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Hallo Welt", "Guten Tag");

        await rig.Vm.CopyOriginalCommand.ExecuteAsync();
        await rig.Vm.CopyTranslationCommand.ExecuteAsync();

        Assert.Equal(["Hallo Welt" + Environment.NewLine + "Guten Tag", "EN:Hallo Welt" + Environment.NewLine + "EN:Guten Tag"], rig.Copied);
        Assert.True(rig.Vm.CanCopyOriginal);
        Assert.True(rig.Vm.CanCopyTranslation);
    }

    [Fact]
    public async Task CopyTranslation_IsUnavailable_WhenNothingWasTranslated()
    {
        var rig = Create();
        rig.Detector.Set("こんにちは", "ja");
        rig.Vm.IsEnabled = true;

        await TranslateAsync(rig, "こんにちは");

        Assert.True(rig.Vm.CanCopyOriginal);
        Assert.False(rig.Vm.CanCopyTranslation);
        Assert.False(rig.Vm.CopyTranslationCommand.CanExecute(null));
    }

    [Fact]
    public async Task MissingModel_ShowsAProblemRow_AndANoticeNamingThePackage()
    {
        var rig = Create();
        rig.Detector.Set("こんにちは", "ja");
        rig.Vm.IsEnabled = true;

        await TranslateAsync(rig, "こんにちは");

        var item = Assert.Single(rig.Vm.Items);
        Assert.Equal(TranslationItemKind.Problem, item.Kind);
        Assert.Null(item.Translated);
        Assert.Contains("translate-ja_en", item.Note, StringComparison.Ordinal);
        var notice = Assert.Single(rig.Vm.Notices);
        Assert.False(notice.IsError);
        Assert.Contains("translate-ja_en", notice.Text, StringComparison.Ordinal);
        Assert.Equal(0, rig.Engine.Calls);
    }

    [Fact]
    public async Task EnglishLine_IsMarkedNotNeeded_AndNeverSentToTheEngine()
    {
        var rig = Create();
        rig.Detector.Set("Hello world", "en");
        rig.Vm.IsEnabled = true;

        await TranslateAsync(rig, "Hello world");

        var item = Assert.Single(rig.Vm.Items);
        Assert.Equal(TranslationItemKind.NotNeeded, item.Kind);
        Assert.Null(item.Translated);
        Assert.Equal(0, rig.Engine.Calls);
        Assert.False(rig.Vm.HasNotices);
    }

    [Fact]
    public async Task UncertainLanguage_ShowsANotice_AndManualChoiceTranslates()
    {
        var rig = Create();
        rig.Vm.IsEnabled = true;

        await TranslateAsync(rig, "Mama mia");

        Assert.Equal(TranslationItemKind.Problem, Assert.Single(rig.Vm.Items).Kind);
        Assert.Contains("uncertain language", Assert.Single(rig.Vm.Notices).Text, StringComparison.Ordinal);

        rig.Vm.SelectedSource = rig.Vm.SourceOptions.First(o => o.Code == "de");
        await rig.Vm.WaitForIdleAsync();

        var item = Assert.Single(rig.Vm.Items);
        Assert.Equal("EN:Mama mia", item.Translated);
        Assert.Contains("(chosen)", item.Language, StringComparison.Ordinal);
        Assert.False(rig.Vm.HasNotices);
    }

    [Fact]
    public async Task WorkerDown_ShowsAnErrorNotice_AndOffersToStartIt()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Engine.Failure = new TranslationException(new TranslationError(TranslationErrorKind.WorkerUnavailable, "worker is gone"));
        rig.Vm.IsEnabled = true;

        await TranslateAsync(rig, "Hallo Welt");

        var notice = Assert.Single(rig.Vm.Notices);
        Assert.True(notice.IsError);
        Assert.Contains("worker is not running", notice.Text, StringComparison.Ordinal);
        Assert.Equal(TranslationItemKind.Problem, Assert.Single(rig.Vm.Items).Kind);
    }

    [Fact]
    public async Task SecondPass_ReusesCacheAndChangesOnlyTheAffectedRow()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Detector.Set("Guten Tag", "de");
        rig.Detector.Set("Gute Nacht", "de");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Hallo Welt", "Guten Tag");
        var untouched = rig.Vm.Items[0];
        int textsBefore = rig.Engine.TextsTranslated;
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
        ((System.Collections.Specialized.INotifyCollectionChanged)rig.Vm.Items).CollectionChanged += (_, e) => changes.Add(e);

        await TranslateAsync(rig, "Hallo Welt", "Gute Nacht");

        Assert.Equal(["Hallo Welt", "Gute Nacht"], rig.Vm.Items.Select(i => i.Original));
        Assert.Equal(["EN:Hallo Welt", "EN:Gute Nacht"], rig.Vm.Items.Select(i => i.Translated));
        Assert.Equal(textsBefore + 1, rig.Engine.TextsTranslated); // only the new text reached the engine
        Assert.All(changes, c => Assert.Equal(1, c.NewStartingIndex)); // row 0 was never replaced
        Assert.Equal(untouched.Original, rig.Vm.Items[0].Original);
    }

    [Fact]
    public async Task UnchangedPass_ReplacesNoRows()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Hallo Welt");
        int changes = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)rig.Vm.Items).CollectionChanged += (_, _) => changes++;

        await TranslateAsync(rig, "Hallo Welt");

        Assert.Equal(0, changes);
        Assert.Equal(1, rig.Engine.TextsTranslated);
    }

    [Fact]
    public async Task NewerPass_SupersedesAStalePass_WhichPublishesNothing()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Detector.Set("Guten Tag", "de");
        var gate = new TaskCompletionSource();
        rig.Engine.Hold = gate.Task;
        rig.Vm.IsEnabled = true;

        rig.Source.Raise(FakeRecognisedSource.Region("Hallo Welt"));
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = rig.Vm.WaitForIdleAsync();
        rig.Engine.Hold = null;
        rig.Source.Raise(FakeRecognisedSource.Region("Guten Tag"));
        await rig.Vm.WaitForIdleAsync();
        await first;
        gate.TrySetResult();

        Assert.Equal(["Guten Tag"], rig.Vm.Items.Select(i => i.Original));
        Assert.Equal("EN:Guten Tag", rig.Vm.Items[0].Translated);
        Assert.False(rig.Vm.IsTranslating);
    }

    [Fact]
    public async Task PendingRows_ShowWhileTheNewPassRuns()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        var gate = new TaskCompletionSource();
        rig.Engine.Hold = gate.Task;
        rig.Vm.IsEnabled = true;

        rig.Source.Raise(FakeRecognisedSource.Region("Hallo Welt"));
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var pending = Assert.Single(rig.Vm.Items);
        Assert.Equal(TranslationItemKind.Pending, pending.Kind);
        Assert.True(rig.Vm.IsTranslating);

        gate.SetResult();
        await rig.Vm.WaitForIdleAsync();
        Assert.Equal(TranslationItemKind.Translated, Assert.Single(rig.Vm.Items).Kind);
    }

    [Fact]
    public async Task ChangingTheTargetOrSource_RetranslatesWithoutReusingOldRows()
    {
        var rig = Create(catalog: c => c.Add("en", "es"));
        rig.Detector.Set("Hello world", "en");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Hello world");
        Assert.Equal(TranslationItemKind.NotNeeded, Assert.Single(rig.Vm.Items).Kind);

        await UntilAsync(() => rig.Vm.TargetOptions.Any(o => o.Code == "es"));
        rig.Vm.SelectedTarget = rig.Vm.TargetOptions.First(o => o.Code == "es");
        await rig.Vm.WaitForIdleAsync();

        var item = Assert.Single(rig.Vm.Items);
        Assert.Equal(TranslationItemKind.Translated, item.Kind);
        Assert.Equal("EN:Hello world", item.Translated);
    }

    [Fact]
    public async Task TurningTranslationOff_ClearsTheRows()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Hallo Welt");

        rig.Vm.IsEnabled = false;

        Assert.Empty(rig.Vm.Items);
        Assert.False(rig.Vm.CanCopyOriginal);
        Assert.False(rig.Vm.HasNotices);
    }

    [Fact]
    public async Task CaptureStopping_ClearsRowsAndTheCaches()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Hallo Welt");

        rig.Source.RaiseDiscarded();
        await rig.Vm.WaitForIdleAsync();
        await TranslateAsync(rig, "Hallo Welt");

        Assert.Equal(2, rig.Engine.TextsTranslated); // the cache was cleared, so the same text was translated again
        Assert.Equal(2, rig.Detector.TextsDetected);
    }

    [Fact]
    public async Task Catalog_PopulatesModelsAndLanguageOptions_WhenTheWorkerIsReady()
    {
        var rig = Create(catalog: c => c.Add("fr", "en", "1.0"));

        Assert.True(await UntilAsync(() => rig.Vm.Packages.Count == 2));

        Assert.Equal("2 models installed.", rig.Vm.ModelsSummary);
        Assert.Equal(@"C:\Users\test\AppData\Local\GlyphLens\models", rig.Vm.StoragePath);
        Assert.Equal(["German → English", "French → English"], rig.Vm.Packages.Select(p => p.Direction));
        var sources = rig.Vm.SourceOptions.Select(o => o.Code).ToList();
        Assert.Null(sources[0]);
        Assert.Equal(["fr", "de"], sources.Skip(1).Take(2)); // installed languages first, by name: French, German
        Assert.Contains(rig.Vm.SourceOptions, o => o.Code == "ja" && o.Label.Contains("no model", StringComparison.Ordinal));
        Assert.Equal(["en"], rig.Vm.TargetOptions.Select(o => o.Code));
    }

    [Fact]
    public async Task Catalog_WithNoModels_SaysSoHonestly()
    {
        var source = new FakeRecognisedSource();
        var models = new FakeModelCatalog();
        var worker = new FakeWorkerManager();
        var pipeline = new TranslationPipeline(new FakeLanguageDetector(), models, new FakeTranslationEngine(), new LruTranslationCache());
        using var vm = new TranslationViewModel(pipeline, models, worker, source, new FakeModelFilePicker(), null, uiContext: null);

        await worker.StartAsync(CancellationToken.None);

        Assert.True(await UntilAsync(() => vm.ModelsSummary.StartsWith("No translation models", StringComparison.Ordinal)));
        Assert.Empty(vm.Packages);
    }

    [Fact]
    public async Task Catalog_FailureIsReported_NotHidden()
    {
        var source = new FakeRecognisedSource();
        var models = new FakeModelCatalog { CatalogFailure = new WorkerException(WorkerErrorKind.RequestTimeout, "The worker did not answer in time.") };
        var worker = new FakeWorkerManager();
        var pipeline = new TranslationPipeline(new FakeLanguageDetector(), models, new FakeTranslationEngine(), new LruTranslationCache());
        using var vm = new TranslationViewModel(pipeline, models, worker, source, new FakeModelFilePicker(), null, uiContext: null);

        await worker.StartAsync(CancellationToken.None);

        Assert.True(await UntilAsync(() => vm.HasModelsError));
        Assert.Contains("did not answer", vm.ModelsError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerStopping_ResetsTheModelsSummary()
    {
        var rig = Create();
        Assert.True(await UntilAsync(() => rig.Vm.Packages.Count == 1));

        await rig.Worker.StopAsync(CancellationToken.None);

        Assert.Contains("Start the worker", rig.Vm.ModelsSummary, StringComparison.Ordinal);
        Assert.True(rig.Vm.CanStartWorker);
        Assert.False(rig.Vm.CanRefreshModels);
    }

    [Fact]
    public async Task WorkerRestart_RetranslatesWhatIsOnScreen()
    {
        var rig = Create();
        rig.Detector.Set("Hallo Welt", "de");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Hallo Welt");
        int before = rig.Engine.TextsTranslated;

        await rig.Worker.RestartAsync(CancellationToken.None);

        Assert.True(await UntilAsync(() => rig.Engine.TextsTranslated > before || rig.Vm.Items.Count == 1));
        await rig.Vm.WaitForIdleAsync();
        Assert.Equal("EN:Hallo Welt", Assert.Single(rig.Vm.Items).Translated);
    }

    [Fact]
    public async Task ImportModel_InstallsTheChosenFile_RefreshesModels_AndRetranslates()
    {
        var rig = Create(catalog: _ => { });
        rig.Detector.Set("Bonjour le monde", "fr");
        rig.Vm.IsEnabled = true;
        await TranslateAsync(rig, "Bonjour le monde");
        Assert.Equal(TranslationItemKind.Problem, Assert.Single(rig.Vm.Items).Kind);
        rig.Picker.Path = @"C:\Downloads\translate-fr_en-1_0.argosmodel";
        rig.Catalog.ImportResult = new TranslationPackageInfo("fr", "en", "French", "English", "1.0", "translate-fr_en");

        await rig.Vm.ImportModelCommand.ExecuteAsync();
        await rig.Vm.WaitForIdleAsync();

        Assert.Equal([@"C:\Downloads\translate-fr_en-1_0.argosmodel"], rig.Catalog.Imported);
        Assert.Contains("Installed French → English", rig.Vm.ModelsMessage, StringComparison.Ordinal);
        Assert.Contains(rig.Vm.Packages, p => p.PackageName == "translate-fr_en");
        Assert.Equal("EN:Bonjour le monde", Assert.Single(rig.Vm.Items).Translated);
    }

    [Fact]
    public async Task ImportModel_Cancelled_ChangesNothing()
    {
        var rig = Create();
        rig.Picker.Path = null;

        await rig.Vm.ImportModelCommand.ExecuteAsync();

        Assert.Equal(1, rig.Picker.Calls);
        Assert.Empty(rig.Catalog.Imported);
        Assert.False(rig.Vm.HasModelsError);
        Assert.False(rig.Vm.HasModelsMessage);
    }

    [Fact]
    public async Task ImportModel_Failure_ShowsTheReason()
    {
        var rig = Create();
        rig.Picker.Path = @"C:\Downloads\bad.argosmodel";
        rig.Catalog.ImportFailure = new TranslationException(new TranslationError(TranslationErrorKind.Failed, "The file is not a valid Argos model package."));

        await rig.Vm.ImportModelCommand.ExecuteAsync();

        Assert.True(rig.Vm.HasModelsError);
        Assert.Contains("not a valid Argos model", rig.Vm.ModelsError, StringComparison.Ordinal);
        Assert.False(rig.Vm.HasModelsMessage);
        Assert.False(rig.Vm.IsModelsBusy);
    }

    [Fact]
    public async Task ImportModel_StartsAStoppedWorkerFirst()
    {
        var rig = Create(workerReady: false);
        rig.Picker.Path = @"C:\Downloads\translate-de_en.argosmodel";

        await rig.Vm.ImportModelCommand.ExecuteAsync();

        Assert.True(rig.Worker.StartCalls >= 1);
        Assert.Single(rig.Catalog.Imported);
    }

    [Fact]
    public async Task Copy_WhenTheClipboardIsBusy_ReportsItWithoutThrowing()
    {
        var source = new FakeRecognisedSource();
        var detector = new FakeLanguageDetector();
        var models = new FakeModelCatalog();
        models.Add("de", "en");
        var worker = new FakeWorkerManager();
        var pipeline = new TranslationPipeline(detector, models, new FakeTranslationEngine(), new LruTranslationCache());
        using var vm = new TranslationViewModel(
            pipeline,
            models,
            worker,
            source,
            null,
            _ => throw new System.Runtime.InteropServices.ExternalException("busy"),
            uiContext: null);
        detector.Set("Hallo Welt", "de");
        await worker.StartAsync(CancellationToken.None);
        vm.IsEnabled = true;
        source.Raise(FakeRecognisedSource.Region("Hallo Welt"));
        await vm.WaitForIdleAsync();

        await vm.CopyOriginalCommand.ExecuteAsync();

        Assert.Contains("clipboard is busy", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportModel_IsUnavailable_WithoutAFilePicker()
    {
        var source = new FakeRecognisedSource();
        var models = new FakeModelCatalog();
        var pipeline = new TranslationPipeline(new FakeLanguageDetector(), models, new FakeTranslationEngine(), new LruTranslationCache());
        using var vm = new TranslationViewModel(pipeline, models, new FakeWorkerManager(), source, null, null, uiContext: null);

        Assert.False(vm.CanImportModel);
    }

    [Fact]
    public void Dispose_UnsubscribesFromItsSources()
    {
        var rig = Create();

        rig.Vm.Dispose();
        rig.Vm.Dispose();

        Assert.Equal(0, rig.Source.SubscriberCount);
        rig.Source.Raise(FakeRecognisedSource.Region("Hallo"));
        Assert.Empty(rig.Vm.Items);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var rig = Create();

        Assert.Throws<ArgumentNullException>(() => new TranslationViewModel(null!, rig.Catalog, rig.Worker, rig.Source));
        Assert.Throws<ArgumentNullException>(() => new TranslationViewModel(rig.Pipeline, null!, rig.Worker, rig.Source));
        Assert.Throws<ArgumentNullException>(() => new TranslationViewModel(rig.Pipeline, rig.Catalog, null!, rig.Source));
        Assert.Throws<ArgumentNullException>(() => new TranslationViewModel(rig.Pipeline, rig.Catalog, rig.Worker, null!));
    }

    [Fact]
    public async Task EngineSummary_ReportsTheWorkersOwnStates()
    {
        var rig = Create();
        await UntilAsync(() => rig.Vm.EngineSummaryText.Contains("Translation:", StringComparison.Ordinal));

        Assert.Contains("Language ID: NOT_IMPLEMENTED", rig.Vm.EngineSummaryText, StringComparison.Ordinal);

        await rig.Worker.StopAsync(CancellationToken.None);
        Assert.Equal("Worker: STOPPED", rig.Vm.EngineSummaryText);
    }

    [Fact]
    public void MainViewModel_ExposesTranslation_AndDisposesIt()
    {
        var rig = Create();
        var capture = new CaptureViewModel(new FakeCaptureService(), new FakeTargetProvider(), null);
        var ocr = new FakeOcr();
        var ocrVm = new OcrViewModel(ocr, ocr, new FakeCaptureService(), capture.Overlay, null, null, TimeSpan.FromMilliseconds(10));
        var main = new MainViewModel(capture, ocrVm, rig.Vm, new OverlayViewModel(rig.Vm), new WorkerViewModel(rig.Worker, null));

        Assert.Same(rig.Vm, main.Translation);
        Assert.Contains("local", main.FeatureStatus, StringComparison.OrdinalIgnoreCase);

        main.Dispose();
        Assert.Equal(0, rig.Source.SubscriberCount);
    }
}
