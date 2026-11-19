using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

/// <summary>
/// Lifecycle logic of <see cref="WorkerProcessManager"/> against scriptable fake workers that speak the real protocol
/// over real named pipes. (The real Python worker is covered by the integration tests in GlyphLens.Windows.Tests.)
/// </summary>
public class WorkerProcessManagerTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(15);

    private static WorkerManagerOptions Fast() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        InitializeTimeout = TimeSpan.FromSeconds(3),
        RequestTimeout = TimeSpan.FromSeconds(3),
        ShutdownRequestTimeout = TimeSpan.FromSeconds(1),
        GracefulExitTimeout = TimeSpan.FromSeconds(1),
        RestartBackoffInitial = TimeSpan.FromMilliseconds(20),
        RestartBackoffMax = TimeSpan.FromMilliseconds(80),
    };

    private static WorkerProcessManager Create(FakeLauncher launcher, WorkerManagerOptions? options = null, RecordingLog? log = null, TestPipeListenerFactory? listeners = null) =>
        new(listeners ?? new TestPipeListenerFactory(), launcher, options ?? Fast(), log);

    private static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Long);
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

    [Fact]
    public async Task NewManager_IsStopped_WithNothingRunning()
    {
        await using var manager = Create(new FakeLauncher());

        Assert.Equal(WorkerStatus.Stopped, manager.Status);
        Assert.Null(manager.ProcessId);
        Assert.Null(manager.LastError);
        Assert.Null(manager.LastHealth);
    }

    [Fact]
    public async Task Start_ConnectsHandshakesAndReportsHealth()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        var statuses = new List<WorkerStatus>();
        manager.StatusChanged += (_, e) => statuses.Add(e.CurrentStatus);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.Equal(launcher.Processes[0].Id, manager.ProcessId);
        Assert.Equal([WorkerStatus.Starting, WorkerStatus.Ready], statuses);
        var health = manager.LastHealth!;
        Assert.Equal(WorkerComponentState.Ready, health.Process);
        Assert.Equal(WorkerComponentState.Connected, health.Ipc);
        Assert.Equal(WorkerComponentState.Initialized, health.Protocol);
        Assert.Equal(WorkerComponentState.NotImplemented, health.Ocr);
        Assert.Equal(WorkerComponentState.NotImplemented, health.LanguageDetection);
        Assert.Equal(WorkerComponentState.NotImplemented, health.Translation);
        Assert.Equal(WorkerComponentState.NotInstalled, health.Models);
        Assert.Null(manager.LastError);
    }

    [Fact]
    public async Task Ready_DoesNotImplyAnyEngineIsUsable()
    {
        await using var manager = Create(new FakeLauncher());

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.DoesNotContain(WorkerComponentState.Ready, new[] { manager.LastHealth!.Ocr, manager.LastHealth.LanguageDetection, manager.LastHealth.Translation });
    }

    [Fact]
    public async Task GetHealth_QueriesTheWorkerAndUpdatesLastHealth()
    {
        await using var manager = Create(new FakeLauncher());
        await manager.StartAsync(CancellationToken.None);

        var health = await manager.GetHealthAsync(CancellationToken.None);

        Assert.Equal(manager.ProcessId, health.ProcessId);
        Assert.Same(health, manager.LastHealth);
    }

    [Fact]
    public async Task GetHealth_WhenNotRunning_ThrowsNotConnected()
    {
        await using var manager = Create(new FakeLauncher());

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.GetHealthAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.NotConnected, ex.Kind);
    }

    [Fact]
    public async Task Start_WhenAlreadyReady_DoesNothing()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task ConcurrentStarts_StartExactlyOneWorker()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => manager.StartAsync(CancellationToken.None)));

        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(WorkerStatus.Ready, manager.Status);
    }

    [Fact]
    public async Task Stop_ShutsTheWorkerDownGracefully_WithoutKilling()
    {
        var launcher = new FakeLauncher();
        var listeners = new TestPipeListenerFactory();
        var log = new RecordingLog();
        await using var manager = Create(launcher, log: log, listeners: listeners);
        await manager.StartAsync(CancellationToken.None);
        var process = launcher.Processes[0];

        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Stopped, manager.Status);
        Assert.Null(manager.ProcessId);
        Assert.Null(manager.LastHealth);
        Assert.Equal(0, await process.Exited);
        Assert.False(process.WasKilled);
        Assert.True(process.Disposed);
        Assert.True(listeners.Listeners[0].IsDisposed);
        Assert.Contains(log.Requests, r => r.MessageType == "worker.shutdown" && r.Outcome == "ok");
    }

    [Fact]
    public async Task Stop_KillsAWorkerThatIgnoresShutdown()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { IgnoreShutdown = true });
        await using var manager = Create(launcher, Fast() with { GracefulExitTimeout = TimeSpan.FromMilliseconds(200) });
        await manager.StartAsync(CancellationToken.None);

        await manager.StopAsync(CancellationToken.None);

        Assert.True(launcher.Processes[0].WasKilled);
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    [Fact]
    public async Task Stop_KillsAWorkerThatDoesNotEvenAcknowledgeShutdown()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { NoShutdownAck = true });
        await using var manager = Create(launcher, Fast() with { ShutdownRequestTimeout = TimeSpan.FromMilliseconds(200), GracefulExitTimeout = TimeSpan.FromMilliseconds(200) });
        await manager.StartAsync(CancellationToken.None);

        await manager.StopAsync(CancellationToken.None);

        Assert.True(launcher.Processes[0].WasKilled);
    }

    [Fact]
    public async Task Stop_WhenNeverStarted_IsHarmless()
    {
        await using var manager = Create(new FakeLauncher());

        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    [Fact]
    public async Task RepeatedStartStop_UsesAFreshProcessAndListenerEachTime()
    {
        var launcher = new FakeLauncher();
        var listeners = new TestPipeListenerFactory();
        await using var manager = Create(launcher, listeners: listeners);

        for (var i = 0; i < 6; i++)
        {
            await manager.StartAsync(CancellationToken.None);
            Assert.Equal(WorkerStatus.Ready, manager.Status);
            await manager.StopAsync(CancellationToken.None);
        }

        Assert.Equal(6, launcher.LaunchCount);
        Assert.Equal(6, launcher.Processes.Select(p => p.Id).Distinct().Count());
        Assert.Equal(6, listeners.Listeners.Select(l => l.PipeName).Distinct().Count()); // a unique pipe name per session
        Assert.All(listeners.Listeners, l => Assert.True(l.IsDisposed));
    }

    [Fact]
    public async Task Restart_ReplacesTheProcess()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);
        var first = manager.ProcessId;

        await manager.RestartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.NotEqual(first, manager.ProcessId);
        Assert.Equal(2, launcher.LaunchCount);
        Assert.Equal(0, await launcher.Processes[0].Exited);
    }

    // ---- failures while starting ----

    [Fact]
    public async Task WorkerThatNeverConnects_TimesOut_AndIsKilled()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { NeverConnect = true });
        await using var manager = Create(launcher, Fast() with { ConnectTimeout = TimeSpan.FromMilliseconds(300) });

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ConnectTimeout, ex.Kind);
        Assert.Equal(WorkerStatus.Error, manager.Status);
        Assert.Equal(ex.Message, manager.LastError);
        Assert.True(launcher.Processes[0].WasKilled);
        Assert.Null(manager.ProcessId);
    }

    [Fact]
    public async Task WorkerThatExitsBeforeConnecting_ReportsTheExitCode()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { ExitImmediately = 7 });
        await using var manager = Create(launcher);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ProcessExited, ex.Kind);
        Assert.Contains("exit code 7", ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkerStatus.Error, manager.Status);
    }

    [Fact]
    public async Task ExitMessage_IncludesTheWorkersLastErrorOutput()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { ExitImmediately = 3 });
        await using var manager = Create(launcher);
        // The fake process reads its error tail from this property when the manager formats the failure.
        var factory = new TestPipeListenerFactory();
        var withOutput = new FakeLauncherWithOutput(["glyphlens-worker: could not connect: boom"]);
        await using var manager2 = new WorkerProcessManager(factory, withOutput, Fast());

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager2.StartAsync(CancellationToken.None));

        Assert.Contains("could not connect: boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientWithTheWrongToken_IsRejected_NotTrusted()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { WrongToken = true });
        var log = new RecordingLog();
        await using var manager = Create(launcher, Fast() with { ConnectTimeout = TimeSpan.FromMilliseconds(600) }, log);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        // The impostor was dropped, never trusted, and never made the manager Ready. (This fake exits when its pipe is
        // closed, like the real worker, so the failure surfaces as an exit rather than a timeout.)
        Assert.Equal(WorkerErrorKind.ProcessExited, ex.Kind);
        Assert.Contains("exit code 5", ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkerStatus.Error, manager.Status);
        Assert.Null(manager.LastHealth);
        Assert.Contains(log.Events, e => e.Contains("Rejected a pipe client", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientThatNeverAnswersTheHandshake_IsRejected()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { IgnoreInitialize = true });
        var log = new RecordingLog();
        await using var manager = Create(launcher, Fast() with { InitializeTimeout = TimeSpan.FromMilliseconds(150), ConnectTimeout = TimeSpan.FromSeconds(2) }, log);

        await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Contains(log.Events, e => e.Contains("Rejected a pipe client", StringComparison.Ordinal));
        Assert.Equal(WorkerStatus.Error, manager.Status);
    }

    [Fact]
    public async Task IncompatibleProtocolVersion_IsRefused()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { ProtocolVersion = "2.0" });
        await using var manager = Create(launcher);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.Rejected, ex.Kind);
        Assert.Equal("unsupported_protocol_version", ex.ErrorCode);
        Assert.Equal(WorkerStatus.Error, manager.Status);
        Assert.True(launcher.Processes[0].WasKilled);
    }

    [Fact]
    public async Task WorkerThatRefusesInitialize_FailsTheStartImmediately()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { RejectInitializeWith = "unsupported_protocol_version" });
        await using var manager = Create(launcher);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.Rejected, ex.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "A refusal must not be retried until the connect timeout.");
    }

    [Fact]
    public async Task MissingPython_IsUnavailable_AndNotRetried()
    {
        var launcher = new FakeLauncher { ThrowOnLaunch = new WorkerException(WorkerErrorKind.PythonNotFound, "No Python 3.11 or newer was found.") };
        await using var manager = Create(launcher);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.PythonNotFound, ex.Kind);
        Assert.Equal(WorkerStatus.Unavailable, manager.Status);
        Assert.Contains("No Python", manager.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedLauncherFailure_IsWrappedInAWorkerException()
    {
        var launcher = new FakeLauncher { ThrowOnLaunch = new InvalidOperationException("kaboom") };
        await using var manager = Create(launcher);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.LaunchFailed, ex.Kind);
        Assert.Equal(WorkerStatus.Error, manager.Status);
    }

    [Fact]
    public async Task AFailedStart_CanBeRetriedSuccessfully()
    {
        var launcher = new FakeLauncher(attempt => attempt == 1 ? new FakeWorkerBehavior { ExitImmediately = 1 } : new FakeWorkerBehavior());
        await using var manager = Create(launcher);
        await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.Null(manager.LastError);
    }

    // ---- cancellation ----

    [Fact]
    public async Task Start_WithACancelledToken_ThrowsWithoutLaunchingAnything()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartAsync(cts.Token));

        Assert.Equal(0, launcher.LaunchCount);
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    [Fact]
    public async Task Start_CancelledWhileWaitingForTheWorker_StopsTheWorker()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { NeverConnect = true });
        await using var manager = Create(launcher, Fast() with { ConnectTimeout = TimeSpan.FromSeconds(30) });
        using var cts = new CancellationTokenSource();

        var starting = manager.StartAsync(cts.Token);
        Assert.True(await UntilAsync(() => launcher.LaunchCount == 1));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
        Assert.True(launcher.Processes[0].WasKilled);
    }

    [Fact]
    public async Task Stop_CancelsAnInFlightStart_WithoutWaitingForTheConnectTimeout()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { NeverConnect = true });
        await using var manager = Create(launcher, Fast() with { ConnectTimeout = TimeSpan.FromSeconds(30) });
        var starting = manager.StartAsync(CancellationToken.None);
        Assert.True(await UntilAsync(() => launcher.LaunchCount == 1));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await manager.StopAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    // ---- crashes and restarts ----

    [Fact]
    public async Task Crash_IsDetectedAndTheWorkerRestarted()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        var errors = new List<string?>();
        manager.StatusChanged += (_, e) =>
        {
            if (e.CurrentStatus == WorkerStatus.Error)
            {
                lock (errors)
                {
                    errors.Add(e.Reason);
                }
            }
        };
        await manager.StartAsync(CancellationToken.None);
        var crashed = manager.ProcessId;

        launcher.Processes[0].Crash(code: 139);

        Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Ready && manager.ProcessId is not null && manager.ProcessId != crashed));
        lock (errors)
        {
            Assert.Contains("exit code 139", Assert.Single(errors), StringComparison.Ordinal);
        }

        Assert.Equal(2, launcher.LaunchCount);
        Assert.True(launcher.Processes[0].Disposed);
        Assert.Equal(WorkerComponentState.Connected, (await manager.GetHealthAsync(CancellationToken.None)).Ipc);
    }

    [Fact]
    public async Task LossOfTheConnectionAlone_IsAlsoTreatedAsAFailure()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);
        var first = manager.ProcessId;

        launcher.Processes[0].CrashHook!.Invoke(); // pipe drops, process stays alive for a moment

        Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Ready && manager.ProcessId is not null && manager.ProcessId != first));
    }

    [Fact]
    public async Task RestartBudget_IsBounded()
    {
        var launcher = new FakeLauncher(attempt => attempt == 1 ? new FakeWorkerBehavior() : new FakeWorkerBehavior { ExitImmediately = 1 });
        await using var manager = Create(launcher, Fast() with { MaxRestartAttempts = 3 });
        await manager.StartAsync(CancellationToken.None);

        launcher.Processes[0].Crash();

        Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Unavailable));
        Assert.Equal(1 + 3, launcher.LaunchCount);
        Assert.Contains("automatic restart stopped", manager.LastError, StringComparison.Ordinal);

        await Task.Delay(400);
        Assert.Equal(4, launcher.LaunchCount); // no further attempts
    }

    [Fact]
    public async Task Backoff_GrowsExponentiallyUpToTheCap()
    {
        var launcher = new FakeLauncher(attempt => attempt == 1 ? new FakeWorkerBehavior() : new FakeWorkerBehavior { ExitImmediately = 1 });
        var log = new RecordingLog();
        await using var manager = Create(
            launcher,
            Fast() with { MaxRestartAttempts = 5, RestartBackoffInitial = TimeSpan.FromMilliseconds(10), RestartBackoffMax = TimeSpan.FromMilliseconds(40) },
            log);
        await manager.StartAsync(CancellationToken.None);

        launcher.Processes[0].Crash();
        Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Unavailable));

        var delays = log.Events
            .Where(e => e.StartsWith("Restarting the worker in", StringComparison.Ordinal))
            .Select(e => double.Parse(e.Split(' ')[4], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal([0.01, 0.02, 0.04, 0.04, 0.04], delays);
    }

    [Fact]
    public async Task AWorkerThatRanStably_DoesNotCountAgainstTheRestartBudget()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher, Fast() with { MaxRestartAttempts = 1, StableUptime = TimeSpan.Zero });
        await manager.StartAsync(CancellationToken.None);

        for (var i = 0; i < 4; i++)
        {
            var crashed = manager.ProcessId;
            launcher.Processes[^1].Crash();
            Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Ready && manager.ProcessId is not null && manager.ProcessId != crashed), $"crash #{i + 1} was not recovered");
        }

        Assert.Equal(5, launcher.LaunchCount); // never gave up although the budget is 1
    }

    [Fact]
    public async Task IntentionalStop_DoesNotRestart()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        await manager.StopAsync(CancellationToken.None);
        await Task.Delay(400);

        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    [Fact]
    public async Task Stop_DuringBackoff_CancelsThePendingRestart()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher, Fast() with { RestartBackoffInitial = TimeSpan.FromSeconds(30), RestartBackoffMax = TimeSpan.FromSeconds(30) });
        await manager.StartAsync(CancellationToken.None);
        launcher.Processes[0].Crash();
        Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Error));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await manager.StopAsync(CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
        await Task.Delay(300);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task ExplicitStart_AfterGivingUp_ResetsTheBudget()
    {
        var launcher = new FakeLauncher(attempt => attempt is 1 or > 4 ? new FakeWorkerBehavior() : new FakeWorkerBehavior { ExitImmediately = 1 });
        await using var manager = Create(launcher, Fast() with { MaxRestartAttempts = 3 });
        await manager.StartAsync(CancellationToken.None);
        launcher.Processes[0].Crash();
        Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Unavailable));

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
    }

    [Fact]
    public async Task HungWorker_ThatStopsAnsweringHealthChecks_IsRestarted()
    {
        var launcher = new FakeLauncher(attempt => attempt == 1 ? new FakeWorkerBehavior { IgnoreLaterHealthRequests = true } : new FakeWorkerBehavior());
        await using var manager = Create(launcher, Fast() with { RequestTimeout = TimeSpan.FromMilliseconds(150), MaxConsecutiveHealthTimeouts = 3 });
        await manager.StartAsync(CancellationToken.None);
        var hung = manager.ProcessId;

        for (var i = 0; i < 3; i++)
        {
            var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.GetHealthAsync(CancellationToken.None));
            Assert.Equal(WorkerErrorKind.RequestTimeout, ex.Kind);
        }

        Assert.True(await UntilAsync(() => manager.Status == WorkerStatus.Ready && manager.ProcessId is not null && manager.ProcessId != hung));
        Assert.True(launcher.Processes[0].WasKilled);
    }

    [Fact]
    public async Task OneHealthTimeout_DoesNotRestartTheWorker()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { IgnoreLaterHealthRequests = true });
        await using var manager = Create(launcher, Fast() with { RequestTimeout = TimeSpan.FromMilliseconds(150) });
        await manager.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<WorkerException>(() => manager.GetHealthAsync(CancellationToken.None));
        await Task.Delay(300);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.Equal(1, launcher.LaunchCount);
    }

    // ---- shutdown, events, logging ----

    [Fact]
    public async Task Dispose_StopsTheWorker_AndStartIsRefusedAfterwards()
    {
        var launcher = new FakeLauncher();
        var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        await manager.DisposeAsync();

        Assert.Equal(0, await launcher.Processes[0].Exited);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(CancellationToken.None));
        await manager.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task ASubscriberThatThrows_DoesNotBreakSupervision()
    {
        var launcher = new FakeLauncher();
        await using var manager = Create(launcher);
        var calls = 0;
        manager.StatusChanged += (_, _) => throw new InvalidOperationException("bad subscriber");
        manager.StatusChanged += (_, _) => calls++;

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StatusEvents_CarryTheFailureReason()
    {
        var launcher = new FakeLauncher(_ => new FakeWorkerBehavior { ExitImmediately = 4 });
        await using var manager = Create(launcher);
        WorkerStatusChangedEventArgs? error = null;
        manager.StatusChanged += (_, e) =>
        {
            if (e.CurrentStatus == WorkerStatus.Error)
            {
                error = e;
            }
        };

        await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.NotNull(error);
        Assert.Equal(WorkerStatus.Starting, error.PreviousStatus);
        Assert.Contains("exit code 4", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsAreLogged_WithIdTypeAndOutcome_ButNoPayload()
    {
        var log = new RecordingLog();
        await using var manager = Create(new FakeLauncher(), log: log);

        await manager.StartAsync(CancellationToken.None);
        await manager.GetHealthAsync(CancellationToken.None);

        Assert.Contains(log.Requests, r => r.MessageType == "worker.initialize" && r.Outcome == "ok");
        Assert.Equal(2, log.Requests.Count(r => r.MessageType == "worker.health" && r.Outcome == "ok"));
        Assert.All(log.Requests, r => Assert.False(string.IsNullOrEmpty(r.RequestId)));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkerProcessManager(null!, new FakeLauncher()));
        Assert.Throws<ArgumentNullException>(() => new WorkerProcessManager(new TestPipeListenerFactory(), null!));
    }

    [Fact]
    public void Options_HaveSensibleDefaults()
    {
        var options = new WorkerManagerOptions();

        Assert.Equal(TimeSpan.FromSeconds(15), options.ConnectTimeout);
        Assert.Equal(5, options.MaxRestartAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RestartBackoffInitial);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RestartBackoffMax);
        Assert.Equal(3, options.MaxRejectedClients);
    }

    private sealed class FakeLauncherWithOutput(string[] lines) : GlyphLens.Core.Abstractions.IWorkerProcessLauncher
    {
        public GlyphLens.Core.Abstractions.IWorkerProcess Launch(GlyphLens.Core.Abstractions.WorkerLaunchSpec spec)
        {
            var process = new FakeWorkerProcess { RecentErrorOutput = lines };
            process.Exit(3);
            return process;
        }
    }
}
