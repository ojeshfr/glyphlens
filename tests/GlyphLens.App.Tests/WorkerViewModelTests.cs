using GlyphLens.App.ViewModels;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

public class WorkerViewModelTests
{
    private static (WorkerViewModel Vm, FakeWorkerManager Manager) Create()
    {
        var manager = new FakeWorkerManager();
        return (new WorkerViewModel(manager, uiContext: null), manager);
    }

    [Fact]
    public void InitialState_IsStopped_WithNoWorkerInformation()
    {
        var (vm, _) = Create();

        Assert.Equal(WorkerStatus.Stopped, vm.Status);
        Assert.Equal("Stopped", vm.StatusText);
        Assert.Equal("NOT CONNECTED", vm.IpcText);
        Assert.Equal("—", vm.ProcessIdText);
        Assert.Equal("—", vm.WorkerProcessText);
        Assert.Equal("—", vm.OcrText);
        Assert.Equal("—", vm.ProtocolVersionText);
        Assert.False(vm.HasError);
        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.False(vm.CanRestart);
        Assert.False(vm.CanRefreshHealth);
    }

    [Fact]
    public async Task Start_ShowsTheWorkersOwnTruthfulReport()
    {
        var (vm, manager) = Create();

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(1, manager.StartCalls);
        Assert.Equal("Ready", vm.StatusText);
        Assert.Equal("CONNECTED", vm.IpcText);
        Assert.Equal("4321", vm.ProcessIdText);
        Assert.Equal("READY", vm.WorkerProcessText);
        Assert.Equal("CONNECTED", vm.WorkerIpcText);
        Assert.Equal("INITIALIZED", vm.ProtocolStateText);
        Assert.Equal("NOT_IMPLEMENTED", vm.OcrText);
        Assert.Equal("NOT_IMPLEMENTED", vm.LanguageDetectionText);
        Assert.Equal("NOT_IMPLEMENTED", vm.TranslationText);
        Assert.Equal("NOT_INSTALLED", vm.ModelsText);
        Assert.Equal("1.0", vm.ProtocolVersionText);
        Assert.Equal("0.2.0", vm.WorkerVersionText);
        Assert.Equal("3.12.10", vm.PythonVersionText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task ARunningWorker_IsNeverPresentedAsAWorkingEngine()
    {
        var (vm, _) = Create();

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal("Ready", vm.StatusText);
        Assert.DoesNotContain("READY", new[] { vm.OcrText, vm.LanguageDetectionText, vm.TranslationText });
    }

    [Fact]
    public async Task ButtonStates_FollowTheWorkersState()
    {
        var (vm, _) = Create();

        await vm.StartCommand.ExecuteAsync();

        Assert.False(vm.CanStart);
        Assert.True(vm.CanStop);
        Assert.True(vm.CanRestart);
        Assert.True(vm.CanRefreshHealth);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));

        await vm.StopCommand.ExecuteAsync();

        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.Equal("—", vm.ProcessIdText);
        Assert.Equal("—", vm.OcrText);
    }

    [Fact]
    public async Task WhileStarting_OnlyStopIsOffered()
    {
        var (vm, manager) = Create();
        var gate = new TaskCompletionSource();
        manager.StartGate = gate.Task;

        var starting = vm.StartCommand.ExecuteAsync();

        Assert.Equal("Starting…", vm.StatusText);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanRestart);
        Assert.False(vm.CanRefreshHealth);
        Assert.False(vm.StartCommand.CanExecute(null));

        gate.SetResult();
        await starting;
        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public async Task OtherCommandsAreDisabled_WhileOneIsRunning()
    {
        var (vm, manager) = Create();
        await vm.StartCommand.ExecuteAsync();
        var gate = new TaskCompletionSource();
        manager.StartGate = gate.Task;

        var restarting = vm.RestartCommand.ExecuteAsync();

        Assert.False(vm.CanStop);
        Assert.False(vm.CanRestart);
        Assert.False(vm.CanRefreshHealth);

        gate.SetResult();
        await restarting;
        Assert.True(vm.CanStop);
    }

    [Fact]
    public async Task StartFailure_IsShownAsAnError_NotThrown()
    {
        var (vm, manager) = Create();
        manager.FailStartWith = new WorkerException(WorkerErrorKind.PythonNotFound, "No Python 3.11 or newer was found.");

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal("Error", vm.StatusText);
        Assert.True(vm.HasError);
        Assert.Equal("No Python 3.11 or newer was found.", vm.LastError);
        Assert.True(vm.CanStart); // can retry after fixing the problem
        Assert.Equal("NOT CONNECTED", vm.IpcText);
    }

    [Fact]
    public async Task AnUnexpectedExceptionBecomesAnError_NotACrash()
    {
        var (vm, manager) = Create();
        await vm.StartCommand.ExecuteAsync();
        manager.FailHealthWith = new InvalidOperationException("kaboom with detail");

        await vm.RefreshHealthCommand.ExecuteAsync();

        Assert.True(vm.HasError);
        Assert.Contains("InvalidOperationException", vm.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("detail", vm.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandError_IsClearedWhenTheNextCommandStarts()
    {
        var (vm, manager) = Create();
        manager.FailStartWith = new WorkerException(WorkerErrorKind.LaunchFailed, "first failure");
        await vm.StartCommand.ExecuteAsync();
        Assert.True(vm.HasError);

        manager.FailStartWith = null;
        await vm.StartCommand.ExecuteAsync();

        Assert.False(vm.HasError);
        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public async Task ACrash_IsShown_WithTheReason()
    {
        var (vm, manager) = Create();
        await vm.StartCommand.ExecuteAsync();

        manager.Crash("The worker process exited unexpectedly (exit code 1).");

        Assert.Equal("Error", vm.StatusText);
        Assert.Equal("NOT CONNECTED", vm.IpcText);
        Assert.Equal("—", vm.ProcessIdText);
        Assert.Equal("—", vm.OcrText); // stale health is not shown for a dead worker
        Assert.Contains("exit code 1", vm.LastError, StringComparison.Ordinal);
        Assert.True(vm.CanStart);
        Assert.True(vm.CanStop); // lets the user cancel a pending automatic restart
    }

    [Fact]
    public async Task RefreshHealth_QueriesTheWorker()
    {
        var (vm, manager) = Create();
        await vm.StartCommand.ExecuteAsync();

        await vm.RefreshHealthCommand.ExecuteAsync();

        Assert.Equal(1, manager.HealthCalls);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task RefreshHealth_FailureIsShown()
    {
        var (vm, manager) = Create();
        await vm.StartCommand.ExecuteAsync();
        manager.FailHealthWith = new WorkerException(WorkerErrorKind.RequestTimeout, "The worker did not answer 'worker.health' within 5 s.");

        await vm.RefreshHealthCommand.ExecuteAsync();

        Assert.Equal("The worker did not answer 'worker.health' within 5 s.", vm.LastError);
    }

    [Fact]
    public async Task Restart_ReplacesTheWorker()
    {
        var (vm, manager) = Create();
        await vm.StartCommand.ExecuteAsync();

        await vm.RestartCommand.ExecuteAsync();

        Assert.Equal(1, manager.RestartCalls);
        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public async Task DisposingTheViewModel_CancelsAPendingStart_WithoutAnError()
    {
        var (vm, manager) = Create();
        manager.StartGate = new TaskCompletionSource().Task; // never completes
        var starting = vm.StartCommand.ExecuteAsync();

        vm.Dispose();
        await starting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.HasError);
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    [Fact]
    public async Task AfterDispose_ManagerEventsDoNotUpdateTheView()
    {
        var (vm, manager) = Create();
        vm.Dispose();
        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ManagerEventsFromAnotherThread_AreMarshalledToTheUiContext()
    {
        var manager = new FakeWorkerManager();
        var context = new RecordingContext();
        using var vm = new WorkerViewModel(manager, context);
        var changed = 0;
        vm.PropertyChanged += (_, _) => changed++;

        await Task.Run(() => manager.StartAsync(CancellationToken.None));

        Assert.True(context.PostCount >= 2);
        Assert.Equal(0, changed); // nothing was applied on the worker thread
        context.RunPending();
        Assert.True(changed > 0);
        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public void PropertyChanged_IsRaisedForAllBoundState_OnEveryRefresh()
    {
        var (vm, _) = Create();
        var names = new List<string?>();
        vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        vm.Refresh();

        Assert.Contains(string.Empty, names); // empty name = every property changed
    }

    [Fact]
    public void Constructor_RejectsANullManager()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkerViewModel(null!));
    }

    [Fact]
    public void MainViewModel_ExposesBothDiagnostics_AndDisposesThem()
    {
        var manager = new FakeWorkerManager();
        var capture = new CaptureViewModel(new FakeCaptureService(), new FakeTargetProvider(), null);
        var ocr = new FakeOcr();
        var ocrVm = new OcrViewModel(ocr, ocr, new FakeCaptureService(), capture.Overlay, null, null, TimeSpan.FromMilliseconds(10));
        var worker = new WorkerViewModel(manager, null);
        var main = new MainViewModel(capture, ocrVm, worker);

        Assert.Same(capture, main.Capture);
        Assert.Same(worker, main.Worker);
        Assert.Same(ocrVm, main.Ocr);
        Assert.Contains("worker", main.FeatureStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not implemented", main.FeatureStatus, StringComparison.OrdinalIgnoreCase);

        main.Dispose();
        main.Dispose();
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_pending)
            {
                PostCount++;
                _pending.Enqueue((d, state));
            }
        }

        public void RunPending()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_pending)
                {
                    if (!_pending.TryDequeue(out next))
                    {
                        return;
                    }
                }

                next.Callback(next.State);
            }
        }
    }
}
