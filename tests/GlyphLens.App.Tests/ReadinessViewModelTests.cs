using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.App.Tests;

public sealed class ReadinessViewModelTests
{
    private const string LocalTag = OcrEngineSelector.LocalEngineTag;

    private static readonly string[] NotCheckedYet = ["python", "ocr", "model-zh", "model-zt"];

    /// <summary>Collects what the view model posts to "the UI thread"; the test runs it on its own thread with <see cref="Pump"/>.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Pump()
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }

    private sealed class FakeProbe : IProcessConnectionProbe
    {
        public ProcessConnections Result { get; set; } = new(0, 2, 0, []);

        public List<IReadOnlyCollection<int>> Calls { get; } = [];

        public ProcessConnections Probe(IReadOnlyCollection<int> processIds)
        {
            Calls.Add(processIds);
            return Result;
        }
    }

    private sealed record Rig(
        ReadinessViewModel Vm,
        TranslationViewModel Translation,
        OcrViewModel Ocr,
        CaptureViewModel Capture,
        FakeWorkerManager Worker,
        FakeModelCatalog Catalog,
        FakeCaptureService CaptureService,
        FakeProbe Probe,
        QueueContext Ui) : IDisposable
    {
        public void Dispose()
        {
            Vm.Dispose();
            Translation.Dispose();
            Ocr.Dispose();
            Capture.Dispose();
        }

        public ReadinessItem Row(string id) => Vm.Items.Single(i => i.Id == id);
    }

    private static Rig Create(bool zh = true, bool zt = false, bool withProbe = true, SetupViewModel? setup = null, Func<bool>? managedRuntimeInstalled = null)
    {
        var engine = new FakeOcr { AvailableLanguages = [new("en-US", "English (United States)"), new(LocalTag, "Chinese + English (local OCR worker)")] };
        var captureService = new FakeCaptureService();
        var ocr = new OcrViewModel(engine, engine, captureService, new OverlayState(), null, null, TimeSpan.FromMilliseconds(10));
        var capture = new CaptureViewModel(captureService, new FakeTargetProvider(), uiContext: null);

        var catalog = new FakeModelCatalog();
        if (zh)
        {
            catalog.Add("zh", "en");
        }

        if (zt)
        {
            catalog.Add("zt", "en");
        }

        var worker = new FakeWorkerManager();
        var pipeline = new TranslationPipeline(new FakeLanguageDetector(), catalog, new FakeTranslationEngine(), new LruTranslationCache());
        var translation = new TranslationViewModel(pipeline, catalog, worker, ocr, new FakeModelFilePicker(), _ => { }, uiContext: null);
        var probe = new FakeProbe();
        var ui = new QueueContext();
        var vm = new ReadinessViewModel(translation, ocr, capture, worker, withProbe ? probe : null, ui, setup: setup, managedRuntimeInstalled: managedRuntimeInstalled);
        return new Rig(vm, translation, ocr, capture, worker, catalog, captureService, probe, ui);
    }

    private static async Task StartAsync(Rig rig, WorkerComponentState ocr = WorkerComponentState.Ready)
    {
        rig.Worker.ReportedOcrState = ocr;
        await rig.Worker.StartAsync(CancellationToken.None);
        await rig.Translation.RefreshModelsCommand.ExecuteAsync();
        await SettleAsync(rig);
    }

    /// <summary>Runs what was posted to the UI and waits for the network check (which runs off-thread) to have answered.</summary>
    private static async Task SettleAsync(Rig rig)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            rig.Ui.Pump();
            if (rig.Probe.Calls.Count > 0 && rig.Row("network").Level != StatusLevel.Neutral)
            {
                return;
            }

            await Task.Delay(10);
        }
        while (DateTime.UtcNow < deadline);
        rig.Ui.Pump();
    }

    [Fact]
    public void BeforeTheWorkerRuns_NothingIsClaimed_AndTheWorkerCanBeStarted()
    {
        using var rig = Create();

        Assert.Equal(["python", "worker", "ocr", "model-zh", "model-zt", "network", "capture"], rig.Vm.Items.Select(i => i.Id));
        Assert.All(NotCheckedYet, id => Assert.Equal(StatusLevel.Neutral, rig.Row(id).Level));
        Assert.Contains("Checked once the worker runs", rig.Row("ocr").Detail, StringComparison.Ordinal);
        Assert.Equal("Start worker", rig.Row("worker").ActionText);
        Assert.Equal(StatusLevel.Neutral, rig.Vm.SummaryLevel);
        Assert.Equal("Not checked yet.", rig.Vm.Summary);
    }

    [Fact]
    public async Task WhenEverythingIsInPlace_TheSummaryIsReady_AndTheRowsAreGreen()
    {
        using var rig = Create(zh: true);
        await StartAsync(rig);

        Assert.Equal(StatusLevel.Ok, rig.Row("python").Level);
        Assert.Equal(StatusLevel.Ok, rig.Row("worker").Level);
        Assert.Equal(StatusLevel.Ok, rig.Row("ocr").Level);
        Assert.Equal(StatusLevel.Ok, rig.Row("model-zh").Level);
        Assert.Contains("version 1.9", rig.Row("model-zh").Detail, StringComparison.Ordinal);
        Assert.Equal(StatusLevel.Ok, rig.Vm.SummaryLevel);
        Assert.Equal("Ready to translate Simplified Chinese.", rig.Vm.Summary);
    }

    [Fact]
    public async Task MissingOcrModels_AreAProblem_WithTheFolderAction()
    {
        using var rig = Create();
        await StartAsync(rig, WorkerComponentState.NotInstalled);

        var row = rig.Row("ocr");

        Assert.Equal(StatusLevel.Problem, row.Level);
        Assert.Equal("Open OCR folder", row.ActionText);
        Assert.Same(rig.Ocr.OpenOcrFolderCommand, row.Action);
        Assert.Equal(StatusLevel.Attention, rig.Vm.SummaryLevel);
        Assert.Contains("Chinese OCR", rig.Vm.SummaryDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingRuntime_IsAProblem_WithoutAnAction()
    {
        using var rig = Create();
        await StartAsync(rig, WorkerComponentState.Unavailable);

        var row = rig.Row("ocr");

        Assert.Equal(StatusLevel.Problem, row.Level);
        Assert.False(row.HasAction);
        Assert.Contains("PaddleOCR", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingSimplifiedModel_NeedsAttention_AndOffersTheImport_WhileTraditionalIsOptional()
    {
        using var rig = Create(zh: false, zt: false);
        await StartAsync(rig);

        Assert.Equal(StatusLevel.Attention, rig.Row("model-zh").Level);
        Assert.Equal("Import model…", rig.Row("model-zh").ActionText);
        Assert.Same(rig.Translation.ImportModelCommand, rig.Row("model-zh").Action);
        Assert.Equal(StatusLevel.Neutral, rig.Row("model-zt").Level);
        Assert.Equal(StatusLevel.Attention, rig.Vm.SummaryLevel);
    }

    [Fact]
    public async Task TheTraditionalModel_IsShownWhenInstalled()
    {
        using var rig = Create(zh: true, zt: true);
        await StartAsync(rig);

        Assert.Equal(StatusLevel.Ok, rig.Row("model-zt").Level);
    }

    [Fact]
    public async Task AWorkerThatFailed_IsAProblem_WithItsReason_AndCanBeStartedAgain()
    {
        using var rig = Create();
        await rig.Worker.StartAsync(CancellationToken.None);
        rig.Worker.Crash("it exited with code 3");

        var row = rig.Row("worker");

        Assert.Equal(StatusLevel.Problem, row.Level);
        Assert.Contains("code 3", row.Detail, StringComparison.Ordinal);
        Assert.Equal("Start worker", row.ActionText);
    }

    [Fact]
    public async Task StoppingTheWorker_ForgetsWhatWasKnown()
    {
        using var rig = Create();
        await StartAsync(rig);
        Assert.Equal(StatusLevel.Ok, rig.Row("ocr").Level);

        await rig.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(StatusLevel.Neutral, rig.Row("ocr").Level);
        Assert.Equal(StatusLevel.Neutral, rig.Row("model-zh").Level);
        Assert.Equal(StatusLevel.Neutral, rig.Vm.SummaryLevel);
    }

    [Fact]
    public async Task TheNetworkCheck_RunsWhenTheWorkerBecomesReady_AndIncludesTheWorkersProcess()
    {
        using var rig = Create();

        await StartAsync(rig);

        var call = Assert.Single(rig.Probe.Calls);
        Assert.Contains(Environment.ProcessId, call);
        Assert.Contains(4321, call);
        var row = rig.Row("network");
        Assert.Equal(StatusLevel.Ok, row.Level);
        Assert.Contains("No remote connections", row.Detail, StringComparison.Ordinal);
        Assert.Equal("Check again", row.ActionText);
    }

    [Fact]
    public async Task RemoteConnections_AreAProblem_WithTheirEndpoints()
    {
        using var rig = Create();
        rig.Probe.Result = new ProcessConnections(1, 0, 0, ["203.0.113.9:443"]);

        await StartAsync(rig);

        var row = rig.Row("network");
        Assert.Equal(StatusLevel.Problem, row.Level);
        Assert.Contains("1 remote connection found", row.Detail, StringComparison.Ordinal);
        Assert.Contains("203.0.113.9:443", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenUdpEndpoints_AreReported_AsAttentionNotAsRemoteConnections()
    {
        using var rig = Create();
        rig.Probe.Result = new ProcessConnections(0, 0, 2, []);

        await StartAsync(rig);

        var row = rig.Row("network");
        Assert.Equal(StatusLevel.Attention, row.Level);
        Assert.Contains("2 local UDP endpoints", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rechecking_ReadsTheConnectionsAgain()
    {
        using var rig = Create();
        await StartAsync(rig);
        rig.Probe.Result = new ProcessConnections(3, 0, 0, ["198.51.100.4:80"]);

        await rig.Vm.RecheckCommand.ExecuteAsync();
        rig.Ui.Pump();

        Assert.Equal(StatusLevel.Problem, rig.Row("network").Level);
        Assert.Equal(2, rig.Probe.Calls.Count);
    }

    [Fact]
    public void WithoutAProbe_TheNetworkRowSaysSo()
    {
        using var rig = Create(withProbe: false);

        Assert.Equal("Not available on this system.", rig.Row("network").Detail);
        Assert.False(rig.Vm.RecheckCommand.CanExecute(null));
    }

    [Fact]
    public void BeforeTheFirstCheck_TheNetworkRowOffersToCheck()
    {
        using var rig = Create();

        var row = rig.Row("network");

        Assert.Equal("Not checked yet.", row.Detail);
        Assert.Equal("Check now", row.ActionText);
    }

    [Fact]
    public async Task TheCaptureRow_FollowsTheCaptureStatus()
    {
        using var rig = Create();
        await rig.Capture.InitializeAsync();
        Assert.Contains("Not capturing", rig.Row("capture").Detail, StringComparison.Ordinal);

        await rig.Capture.StartCommand.ExecuteAsync();

        Assert.Equal(StatusLevel.Ok, rig.Row("capture").Level);
        Assert.StartsWith("Capturing", rig.Row("capture").Detail, StringComparison.Ordinal);

        await rig.Capture.StopCommand.ExecuteAsync();
        Assert.Equal(StatusLevel.Neutral, rig.Row("capture").Level);
    }

    [Fact]
    public async Task ADisposedViewModel_StopsFollowingTheSources()
    {
        var rig = Create();
        rig.Vm.Dispose();

        await rig.Worker.StartAsync(CancellationToken.None);

        Assert.Equal(StatusLevel.Neutral, rig.Row("worker").Level);
        rig.Translation.Dispose();
        rig.Ocr.Dispose();
        rig.Capture.Dispose();
    }

    // ---- the "Install everything" offer ------------------------------------------------------------------------------

    private static SetupViewModel NewSetup() => new(new SetupViewModelTests.FakeSetup(), uiContext: null);

    [Fact]
    public void BeforeTheWorkerHasReported_SetupIsNotOffered()
    {
        var setup = NewSetup();
        using var rig = Create(setup: setup);

        Assert.False(rig.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Hidden, setup.State);
        Assert.True(rig.Vm.HasSetup);
    }

    [Fact]
    public void ANewPc_BeforeTheWorkerHasEverStarted_StillGetsTheOffer()
    {
        // The worker starts only when translation is turned on; without the offer a new user would never find the button.
        var setup = NewSetup();
        using var rig = Create(setup: setup, managedRuntimeInstalled: () => false);

        Assert.True(rig.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Offer, setup.State);
    }

    [Fact]
    public void WithTheSetupsOwnEnvironmentInstalled_NothingIsClaimedBeforeTheWorkerReports()
    {
        var setup = NewSetup();
        using var rig = Create(setup: setup, managedRuntimeInstalled: () => true);

        Assert.False(rig.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Hidden, setup.State);
    }

    [Theory]
    [InlineData(WorkerComponentState.NotInstalled)]
    [InlineData(WorkerComponentState.Unavailable)]
    public async Task MissingOcrPartsOrRuntime_OfferTheSetup(WorkerComponentState ocr)
    {
        var setup = NewSetup();
        using var rig = Create(setup: setup);

        await StartAsync(rig, ocr);

        Assert.True(rig.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Offer, setup.State);
    }

    [Fact]
    public async Task AMissingSimplifiedModel_OffersTheSetup_ButAMissingTraditionalOneDoesNot()
    {
        var setup = NewSetup();
        using var rig = Create(zh: false, zt: false, setup: setup);
        await StartAsync(rig);
        Assert.True(rig.Vm.NeedsSetup);

        var setup2 = NewSetup();
        using var rig2 = Create(zh: true, zt: false, setup: setup2);
        await StartAsync(rig2);
        Assert.False(rig2.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Hidden, setup2.State);
    }

    [Fact]
    public void NoUsablePython_OffersTheSetup()
    {
        var setup = NewSetup();
        using var rig = Create(setup: setup);

        rig.Worker.BecomeUnavailable("No Python 3.11 or newer was found.");
        rig.Ui.Pump();

        Assert.True(rig.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Offer, setup.State);
    }

    [Fact]
    public async Task WhenEverythingIsThere_TheOfferDisappears()
    {
        var setup = NewSetup();
        using var rig = Create(zh: false, setup: setup);
        await StartAsync(rig);
        Assert.Equal(SetupPanelState.Offer, setup.State);

        rig.Catalog.Add("zh", "en");
        await rig.Translation.RefreshModelsCommand.ExecuteAsync();
        rig.Ui.Pump();

        Assert.False(rig.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Hidden, setup.State);
    }

    [Fact]
    public async Task AfterASuccessfulSetup_TheModelsAndConnectionsAreCheckedAgain()
    {
        var fake = new SetupViewModelTests.FakeSetup();
        var setup = new SetupViewModel(fake, uiContext: null);
        using var rig = Create(zh: false, setup: setup);
        await StartAsync(rig);
        int catalogCalls = rig.Catalog.CatalogCalls;
        int probes = rig.Probe.Calls.Count;
        fake.OnRun = () => rig.Catalog.Add("zh", "en");

        setup.ShowConsentCommand.Execute(null);
        await setup.StartCommand.ExecuteAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (rig.Probe.Calls.Count <= probes && DateTime.UtcNow < deadline)
        {
            rig.Ui.Pump();
            await Task.Delay(10);
        }

        await Task.Delay(50);
        rig.Ui.Pump();

        Assert.True(rig.Catalog.CatalogCalls > catalogCalls);
        Assert.True(rig.Probe.Calls.Count > probes);
        Assert.Equal(StatusLevel.Ok, rig.Row("model-zh").Level);
        Assert.False(rig.Vm.NeedsSetup);
        Assert.Equal(SetupPanelState.Succeeded, setup.State); // the success note stays until the user closes it
    }

    [Fact]
    public async Task TheModelRow_PointsAtInstallEverything()
    {
        using var rig = Create(zh: false);
        await StartAsync(rig);

        Assert.Contains("Install everything", rig.Row("model-zh").Detail, StringComparison.Ordinal);
    }
}
