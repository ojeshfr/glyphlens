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

    private static Rig Create(bool zh = true, bool zt = false, bool withProbe = true)
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
        var vm = new ReadinessViewModel(translation, ocr, capture, worker, withProbe ? probe : null, ui);
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
}
