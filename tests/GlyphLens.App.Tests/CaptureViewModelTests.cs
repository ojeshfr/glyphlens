using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Capture;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

public class CaptureViewModelTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static async Task<(CaptureViewModel Vm, FakeCaptureService Service, FakeTargetProvider Provider)> CreateAsync()
    {
        var service = new FakeCaptureService();
        var provider = new FakeTargetProvider();
        var vm = new CaptureViewModel(service, provider, uiContext: null);
        await vm.InitializeAsync();
        return (vm, service, provider);
    }

    private static CapturedFrame Frame(long sequence, int width = 1920, int height = 1080)
    {
        var metadata = new FrameMetadata("display:1", width, height, width * 4, PixelFormat.Bgra8, SampleTime, sequence);
        return new CapturedFrame(metadata, new byte[metadata.ByteLength]);
    }

    [Fact]
    public async Task Initialize_ListsDisplaysAndSelectsThePrimaryOne()
    {
        var (vm, _, provider) = await CreateAsync();

        Assert.Equal(1, provider.Calls);
        Assert.Equal(CaptureTargetKind.Display, vm.SelectedKind);
        Assert.Equal(["Display 1", "Display 2"], vm.Options.Select(o => o.Target.DisplayName));
        Assert.Equal("display:1", vm.SelectedOption?.Target.Id);
        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
    }

    [Fact]
    public async Task SwitchingToWindows_ShowsOnlyWindows_AndSelectsTheFirst()
    {
        var (vm, _, _) = await CreateAsync();

        vm.IsWindowKind = true;

        Assert.Equal(CaptureTargetKind.Window, vm.SelectedKind);
        Assert.False(vm.IsDisplayKind);
        Assert.Equal(["Inbox - Mail", "Report.pdf"], vm.Options.Select(o => o.Target.DisplayName));
        Assert.Equal("window:1", vm.SelectedOption?.Target.Id);
    }

    [Fact]
    public async Task OptionLabels_ShowSizeScalingPrimaryAndProcess()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.Equal("Display 1 (1920×1080, 150%, primary)", vm.Options[0].Label);
        Assert.Equal("Display 2 (1080×1920, 100%)", vm.Options[1].Label); // portrait, not primary

        vm.IsWindowKind = true;
        Assert.Equal("Inbox - Mail — chrome (1280×720)", vm.Options[0].Label);
    }

    [Fact]
    public async Task NoTargetsOfAKind_DisablesStart()
    {
        var (vm, _, provider) = await CreateAsync();
        provider.Targets = [.. provider.Targets.Where(t => t.Kind == CaptureTargetKind.Display)];
        await vm.RefreshCommand.ExecuteAsync();

        vm.IsWindowKind = true;

        Assert.Empty(vm.Options);
        Assert.Null(vm.SelectedOption);
        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_KeepsTheSelectedTargetWhenItStillExists()
    {
        var (vm, _, provider) = await CreateAsync();
        vm.SelectedOption = vm.Options[1];
        provider.Targets.Add(new CaptureTarget("display:3", CaptureTargetKind.Display, "Display 3", 800, 600));

        await vm.RefreshCommand.ExecuteAsync();

        Assert.Equal(3, vm.Options.Count);
        Assert.Equal("display:2", vm.SelectedOption?.Target.Id);
    }

    [Fact]
    public async Task Refresh_FallsBackWhenTheSelectedWindowDisappeared()
    {
        var (vm, _, provider) = await CreateAsync();
        vm.IsWindowKind = true;
        vm.SelectedOption = vm.Options[1];
        provider.Targets.RemoveAll(t => t.Id == "window:2");

        await vm.RefreshCommand.ExecuteAsync();

        Assert.Equal("window:1", vm.SelectedOption?.Target.Id);
    }

    [Fact]
    public async Task Refresh_ReportsAnEnumerationFailure_WithoutThrowing()
    {
        var (vm, _, provider) = await CreateAsync();
        provider.Failure = new InvalidOperationException("enumeration broke");

        await vm.RefreshCommand.ExecuteAsync();

        Assert.True(vm.HasError);
        Assert.Contains("displays and windows", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("enumeration broke", vm.ErrorDetail, StringComparison.Ordinal);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task Start_CapturesTheSelectedTarget_AndReflectsTheServiceState()
    {
        var (vm, service, _) = await CreateAsync();
        vm.SelectedOption = vm.Options[1];

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(["display:2"], service.StartedTargets.Select(t => t.Id));
        Assert.Equal(CaptureStatus.Capturing, vm.Status);
        Assert.True(vm.IsCapturing);
        Assert.Equal("Capturing", vm.StatusText);
        Assert.False(vm.CanStart);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanChooseTarget);
        Assert.Equal("Display 2 (1080×1920, 100%)", vm.TargetInfoText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task WhileStarting_NothingCanBeChangedOrStartedAgain()
    {
        var (vm, service, _) = await CreateAsync();
        var gate = new TaskCompletionSource();
        service.StartGate = gate.Task;

        var starting = vm.StartCommand.ExecuteAsync();

        Assert.Equal(CaptureStatus.Starting, vm.Status);
        Assert.Equal("Starting…", vm.StatusText);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.False(vm.CanChooseTarget);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));

        gate.SetResult();
        await starting;
        Assert.Equal(CaptureStatus.Capturing, vm.Status);
    }

    [Fact]
    public async Task Start_DoubleInvocation_StartsOnlyOnce()
    {
        var (vm, service, _) = await CreateAsync();
        var gate = new TaskCompletionSource();
        service.StartGate = gate.Task;

        var first = vm.StartCommand.ExecuteAsync();
        var second = vm.StartCommand.ExecuteAsync(); // ignored: the command is running
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(service.StartedTargets);
    }

    [Fact]
    public async Task Stop_StopsTheService_ReturnsToIdle_AndClearsTheFrameInfo()
    {
        var (vm, service, _) = await CreateAsync();
        await vm.StartCommand.ExecuteAsync();
        service.Mailbox.Publish(Frame(7));
        vm.RefreshFrameInfo();
        Assert.Equal("7", vm.FrameSequenceText);

        await vm.StopCommand.ExecuteAsync();

        Assert.Equal(1, service.StopCalls);
        Assert.Equal(CaptureStatus.Idle, vm.Status);
        Assert.Equal("Not capturing", vm.StatusText);
        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.Equal("—", vm.FrameSizeText);
        Assert.Equal("—", vm.FrameSequenceText);
        Assert.Equal(string.Empty, vm.TargetInfoText);
    }

    [Fact]
    public async Task TheFrameRate_IsMeasuredFromTheFramesPublished_AndClearedWhenCaptureStops()
    {
        var (vm, service, _) = await CreateAsync();
        Assert.Equal("—", vm.FrameRateText);
        await vm.StartCommand.ExecuteAsync();
        service.Mailbox.Publish(Frame(1));
        vm.RefreshFrameInfo();
        Assert.Equal("—", vm.FrameRateText); // one sample: no rate yet

        await Task.Delay(120);
        for (int i = 2; i <= 6; i++)
        {
            service.Mailbox.Publish(Frame(i));
        }

        vm.RefreshFrameInfo();
        Assert.Matches(@"^\d+\.\d frames/s$", vm.FrameRateText);
        Assert.NotEqual("0.0 frames/s", vm.FrameRateText);

        await vm.StopCommand.ExecuteAsync();
        Assert.Equal("—", vm.FrameRateText);
    }

    [Fact]
    public async Task StartStopStart_RepeatsCleanly()
    {
        var (vm, service, _) = await CreateAsync();

        for (var i = 0; i < 5; i++)
        {
            await vm.StartCommand.ExecuteAsync();
            Assert.Equal(CaptureStatus.Capturing, vm.Status);
            await vm.StopCommand.ExecuteAsync();
            Assert.Equal(CaptureStatus.Idle, vm.Status);
        }

        Assert.Equal(5, service.StartedTargets.Count);
        Assert.Equal(5, service.StopCalls);
    }

    [Fact]
    public async Task FrameInfo_ShowsSizeAndSequenceOfTheLatestFrame_OnlyWhileCapturing()
    {
        var (vm, service, _) = await CreateAsync();
        service.Mailbox.Publish(Frame(3));
        vm.RefreshFrameInfo();
        Assert.Equal("—", vm.FrameSizeText); // not capturing: a leftover frame must not look live

        await vm.StartCommand.ExecuteAsync();
        service.Mailbox.Publish(Frame(1_234, 2560, 1440));
        vm.RefreshFrameInfo();

        Assert.Equal("2560 × 1440 px", vm.FrameSizeText);
        Assert.Equal("1,234", vm.FrameSequenceText);
    }

    [Fact]
    public async Task StartFailure_ShowsAReadableError_AndLeavesTheViewModelUsable()
    {
        var (vm, service, _) = await CreateAsync();
        service.FailStartWith = new CaptureError(CaptureErrorKind.AccessDenied, "Windows denied access.") { Detail = "0x80070005" };

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(CaptureStatus.Faulted, vm.Status);
        Assert.Equal("Capture stopped by an error", vm.StatusText);
        Assert.True(vm.HasError);
        Assert.Equal("Windows denied access.", vm.ErrorMessage);
        Assert.Equal("0x80070005", vm.ErrorDetail);
        Assert.False(vm.IsCapturing);
        Assert.True(vm.CanStart); // can retry or pick another target
        Assert.True(vm.CanChooseTarget);
    }

    [Fact]
    public async Task StartingAgain_ClearsThePreviousError()
    {
        var (vm, service, _) = await CreateAsync();
        service.FailStartWith = new CaptureError(CaptureErrorKind.Unexpected, "first failure");
        await vm.StartCommand.ExecuteAsync();
        Assert.True(vm.HasError);

        service.FailStartWith = null;
        await vm.StartCommand.ExecuteAsync();

        Assert.False(vm.HasError);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(CaptureStatus.Capturing, vm.Status);
    }

    [Theory]
    [InlineData(CaptureErrorKind.TargetNotFound)]
    [InlineData(CaptureErrorKind.TargetClosed)]
    public async Task StaleTargetErrors_RefreshTheTargetList(CaptureErrorKind kind)
    {
        var (vm, service, provider) = await CreateAsync();
        service.FailStartWith = new CaptureError(kind, "gone");
        var callsBefore = provider.Calls;

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(callsBefore + 1, provider.Calls);
    }

    [Fact]
    public async Task OtherErrors_DoNotRefreshTheTargetList()
    {
        var (vm, service, provider) = await CreateAsync();
        service.FailStartWith = new CaptureError(CaptureErrorKind.AccessDenied, "denied");
        var callsBefore = provider.Calls;

        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(callsBefore, provider.Calls);
    }

    [Fact]
    public async Task TargetClosingDuringCapture_ShowsTheErrorAndStopsLooksLive()
    {
        var (vm, service, _) = await CreateAsync();
        await vm.StartCommand.ExecuteAsync();
        service.Mailbox.Publish(Frame(9));
        vm.RefreshFrameInfo();

        service.Fault(new CaptureError(CaptureErrorKind.TargetClosed, "The captured window was closed."));

        Assert.Equal(CaptureStatus.Faulted, vm.Status);
        Assert.False(vm.IsCapturing);
        Assert.Equal("The captured window was closed.", vm.ErrorMessage);
        Assert.Equal("—", vm.FrameSequenceText);
        Assert.Equal(string.Empty, vm.TargetInfoText);
        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
    }

    [Fact]
    public async Task DeviceLostDuringCapture_IsReportedAndRestartIsPossible()
    {
        var (vm, service, _) = await CreateAsync();
        await vm.StartCommand.ExecuteAsync();

        service.Fault(new CaptureError(CaptureErrorKind.DeviceLost, "The graphics device was reset."));
        Assert.Equal("The graphics device was reset.", vm.ErrorMessage);

        await vm.StartCommand.ExecuteAsync();
        Assert.Equal(CaptureStatus.Capturing, vm.Status);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task UnexpectedExceptionFromTheService_BecomesAnErrorNotACrash()
    {
        var service = new ThrowingService();
        var vm = new CaptureViewModel(service, new FakeTargetProvider(), null);
        await vm.InitializeAsync();

        await vm.StartCommand.ExecuteAsync();

        Assert.True(vm.HasError);
        Assert.Contains("could not be started", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("kaboom", vm.ErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingTheViewModel_CancelsAPendingStart_WithoutAnError()
    {
        var (vm, service, _) = await CreateAsync();
        service.StartGate = new TaskCompletionSource().Task; // never completes
        var starting = vm.StartCommand.ExecuteAsync();

        vm.Dispose();
        await starting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.HasError);
        Assert.Equal(CaptureStatus.Idle, service.Status);
    }

    [Fact]
    public async Task AfterDispose_ServiceEventsAreIgnored()
    {
        var (vm, service, _) = await CreateAsync();
        vm.Dispose();

        await service.StartAsync(new CaptureTarget("display:1", CaptureTargetKind.Display, "d", 10, 10, true));

        Assert.Equal(CaptureStatus.Idle, vm.Status);
    }

    [Fact]
    public async Task ChangingTheTargetKindIsBlockedWhileCapturing()
    {
        var (vm, _, _) = await CreateAsync();
        await vm.StartCommand.ExecuteAsync();

        Assert.False(vm.CanChooseTarget);
    }

    [Fact]
    public async Task PropertyChanges_AreRaisedForTheBoundState()
    {
        var (vm, _, _) = await CreateAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.StartCommand.ExecuteAsync();

        Assert.Contains(nameof(CaptureViewModel.Status), changed);
        Assert.Contains(nameof(CaptureViewModel.StatusText), changed);
        Assert.Contains(nameof(CaptureViewModel.IsCapturing), changed);
        Assert.Contains(nameof(CaptureViewModel.CanStart), changed);
        Assert.Contains(nameof(CaptureViewModel.CanStop), changed);
        Assert.Contains(nameof(CaptureViewModel.TargetInfoText), changed);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new CaptureViewModel(null!, new FakeTargetProvider()));
        Assert.Throws<ArgumentNullException>(() => new CaptureViewModel(new FakeCaptureService(), null!));
    }

    [Fact]
    public async Task ServiceEventsFromAnotherThread_AreMarshalledToTheUiContext()
    {
        var service = new FakeCaptureService();
        var context = new RecordingSynchronizationContext();
        var vm = new CaptureViewModel(service, new FakeTargetProvider(), context);

        await Task.Run(() => service.StartAsync(new CaptureTarget("display:1", CaptureTargetKind.Display, "d", 10, 10, true)));

        Assert.True(context.PostCount >= 2); // Starting and Capturing were posted, not applied on the worker thread
        context.RunPending();
        Assert.Equal(CaptureStatus.Capturing, vm.Status);
        vm.Dispose();
    }

    private sealed class ThrowingService : IScreenCaptureService
    {
        private readonly LatestFrameMailbox _mailbox = new();

        public CaptureStatus Status => CaptureStatus.Idle;

        public CaptureTarget? CurrentTarget => null;

        public IFrameSource Frames => _mailbox;

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CaptureTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("kaboom");

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _mailbox.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
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
