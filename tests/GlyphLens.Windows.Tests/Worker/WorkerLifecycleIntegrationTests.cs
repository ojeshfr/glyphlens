using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Windows.Worker;

namespace GlyphLens.Windows.Tests.Worker;

/// <summary>
/// End-to-end tests with the real Python worker process and the real ACL'd Windows named pipe:
/// .NET manager → pipe → Python worker → handshake → health → shutdown. No OCR models or network are involved,
/// and no test claims OCR or translation works.
/// </summary>
public sealed class WorkerLifecycleIntegrationTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static WorkerProcessManager Manager(GlyphLens.Core.Abstractions.IWorkerProcessLauncher launcher, WorkerManagerOptions? options = null) =>
        new(new NamedPipeWorkerListenerFactory(), launcher, options ?? FastOptions());

    private static WorkerManagerOptions FastOptions() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        InitializeTimeout = TimeSpan.FromSeconds(10),
        RequestTimeout = TimeSpan.FromSeconds(10),
        GracefulExitTimeout = TimeSpan.FromSeconds(10),
        RestartBackoffInitial = TimeSpan.FromMilliseconds(100),
        RestartBackoffMax = TimeSpan.FromMilliseconds(400),
    };

    [WorkerIntegrationFact]
    public async Task StartHealthShutdown_FullPipeline_ReportsTruthfulHealth_AndExitsCleanly()
    {
        using var launcher = WorkerTestEnvironment.CreateLauncher();
        await using var manager = Manager(launcher);
        var statuses = new List<WorkerStatus>();
        manager.StatusChanged += (_, e) => statuses.Add(e.CurrentStatus);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        var pid = Assert.IsType<int>(manager.ProcessId);
        Assert.False(ProcessAssert.IsGone(pid));

        var health = await manager.GetHealthAsync(CancellationToken.None);

        // Exactly the truthful report for a build with transport but no engines.
        Assert.Equal(WorkerComponentState.Ready, health.Process);
        Assert.Equal(WorkerComponentState.Connected, health.Ipc);
        Assert.Equal(WorkerComponentState.Initialized, health.Protocol);
        Assert.Equal(WorkerComponentState.NotImplemented, health.Ocr);
        Assert.Equal(WorkerComponentState.NotImplemented, health.LanguageDetection);
        Assert.Equal(WorkerComponentState.NotImplemented, health.Translation);
        Assert.Equal(WorkerComponentState.NotInstalled, health.Models);
        Assert.Equal(pid, health.ProcessId);
        Assert.Equal("1.0", health.ProtocolVersion.ToString());
        Assert.Equal($"{WorkerTestEnvironment.Python.Version.Major}.{WorkerTestEnvironment.Python.Version.Minor}", string.Join('.', health.PythonVersion!.Split('.').Take(2)));
        Assert.Equal(health, manager.LastHealth);

        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Stopped, manager.Status);
        Assert.Null(manager.ProcessId);
        Assert.Null(manager.LastHealth);
        Assert.True(await ProcessAssert.WaitUntilAsync(() => ProcessAssert.IsGone(pid), TimeSpan.FromSeconds(5)), "The worker process must have exited.");
        Assert.Equal([WorkerStatus.Starting, WorkerStatus.Ready, WorkerStatus.Stopped], statuses);
    }

    [WorkerIntegrationFact]
    public async Task RepeatedStartStop_EachCycleUsesAFreshProcess_AndLeavesNothingBehind()
    {
        using var launcher = WorkerTestEnvironment.CreateLauncher();
        await using var manager = Manager(launcher);
        var pids = new List<int>();

        for (var i = 0; i < 5; i++)
        {
            await manager.StartAsync(CancellationToken.None);
            pids.Add(manager.ProcessId!.Value);
            Assert.Equal(WorkerComponentState.Connected, (await manager.GetHealthAsync(CancellationToken.None)).Ipc);
            await manager.StopAsync(CancellationToken.None);
            Assert.Equal(WorkerStatus.Stopped, manager.Status);
        }

        Assert.Equal(5, pids.Distinct().Count());
        foreach (var pid in pids)
        {
            Assert.True(await ProcessAssert.WaitUntilAsync(() => ProcessAssert.IsGone(pid), TimeSpan.FromSeconds(5)), $"Process {pid} still running.");
        }
    }

    [WorkerIntegrationFact]
    public async Task Restart_ReplacesTheWorkerProcess()
    {
        using var launcher = WorkerTestEnvironment.CreateLauncher();
        await using var manager = Manager(launcher);
        await manager.StartAsync(CancellationToken.None);
        var first = manager.ProcessId!.Value;

        await manager.RestartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.NotEqual(first, manager.ProcessId!.Value);
        Assert.True(await ProcessAssert.WaitUntilAsync(() => ProcessAssert.IsGone(first), TimeSpan.FromSeconds(5)));
    }

    [WorkerIntegrationFact]
    public async Task WorkerCrash_IsDetected_AndTheWorkerIsRestartedAutomatically()
    {
        using var launcher = WorkerTestEnvironment.CreateLauncher();
        await using var manager = Manager(launcher);
        var errors = new List<WorkerStatusChangedEventArgs>();
        manager.StatusChanged += (_, e) =>
        {
            lock (errors)
            {
                errors.Add(e);
            }
        };
        await manager.StartAsync(CancellationToken.None);
        var crashed = manager.ProcessId!.Value;

        System.Diagnostics.Process.GetProcessById(crashed).Kill(entireProcessTree: true);

        Assert.True(
            await ProcessAssert.WaitUntilAsync(() => manager.Status == WorkerStatus.Ready && manager.ProcessId != crashed, Generous),
            "The manager should detect the crash and bring up a new worker.");
        lock (errors)
        {
            var error = errors.First(e => e.CurrentStatus == WorkerStatus.Error);
            Assert.Contains("exited unexpectedly", error.Reason, StringComparison.Ordinal);
        }

        Assert.Equal(WorkerComponentState.Connected, (await manager.GetHealthAsync(CancellationToken.None)).Ipc);
    }

    [WorkerIntegrationFact]
    public async Task RestartBudget_IsBounded_AndNothingRestartsForever()
    {
        using var real = WorkerTestEnvironment.CreateLauncher();
        var launcher = new ScriptedLauncher((count, spec) =>
            count == 1
                ? real.Launch(spec)
                : throw new WorkerException(WorkerErrorKind.LaunchFailed, "simulated launch failure"));
        var options = FastOptions() with { MaxRestartAttempts = 3 };
        await using var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher, options);
        await manager.StartAsync(CancellationToken.None);

        System.Diagnostics.Process.GetProcessById(manager.ProcessId!.Value).Kill(entireProcessTree: true);

        Assert.True(await ProcessAssert.WaitUntilAsync(() => manager.Status == WorkerStatus.Unavailable, Generous));
        Assert.Equal(1 + 3, launcher.LaunchCount); // the original start plus exactly MaxRestartAttempts retries
        Assert.Contains("automatic restart stopped", manager.LastError, StringComparison.Ordinal);

        await Task.Delay(1000);
        Assert.Equal(4, launcher.LaunchCount); // and it does not keep trying
    }

    [WorkerIntegrationFact]
    public async Task IntentionalStop_DoesNotTriggerARestart()
    {
        using var real = WorkerTestEnvironment.CreateLauncher();
        var launcher = new ScriptedLauncher((_, spec) => real.Launch(spec));
        await using var manager = Manager(launcher);
        await manager.StartAsync(CancellationToken.None);

        await manager.StopAsync(CancellationToken.None);
        await Task.Delay(1500);

        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    [WorkerIntegrationFact]
    public async Task WorkerThatNeverConnects_TimesOut_AndIsKilled()
    {
        var startedPid = 0;
        var launcher = new ScriptedLauncher((_, _) =>
        {
            var started = WorkerTestEnvironment.StartPython("-c", "import time; time.sleep(60)");
            startedPid = started.Id;
            return new PlainProcess(started);
        });
        var options = FastOptions() with { ConnectTimeout = TimeSpan.FromSeconds(1) };
        await using var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher, options);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ConnectTimeout, ex.Kind);
        Assert.Equal(WorkerStatus.Error, manager.Status);
        Assert.NotEqual(0, startedPid);
        Assert.True(await ProcessAssert.WaitUntilAsync(() => ProcessAssert.IsGone(startedPid), TimeSpan.FromSeconds(5)), "The unresponsive worker must be terminated.");
    }

    [WorkerIntegrationFact]
    public async Task WorkerThatExitsBeforeConnecting_ReportsItsExitCode()
    {
        var launcher = new ScriptedLauncher((_, _) => new PlainProcess(WorkerTestEnvironment.StartPython("-c", "import sys; sys.exit(9)")));
        await using var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher, FastOptions());

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ProcessExited, ex.Kind);
        Assert.Contains("exit code 9", ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkerStatus.Error, manager.Status);
    }

    [WorkerIntegrationFact]
    public async Task StartCancelled_ByStop_ReturnsPromptly_AndLeavesNoProcess()
    {
        var startedPid = 0;
        var launcher = new ScriptedLauncher((_, _) =>
        {
            var started = WorkerTestEnvironment.StartPython("-c", "import time; time.sleep(60)");
            Volatile.Write(ref startedPid, started.Id);
            return new PlainProcess(started);
        });
        await using var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher, FastOptions());

        var starting = manager.StartAsync(CancellationToken.None);
        await ProcessAssert.WaitUntilAsync(() => startedPid != 0, TimeSpan.FromSeconds(10));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await manager.StopAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "Stop must not wait for the 20 s connect timeout.");
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
        Assert.True(await ProcessAssert.WaitUntilAsync(() => ProcessAssert.IsGone(startedPid), TimeSpan.FromSeconds(5)));
    }

    [WorkerIntegrationFact]
    public async Task ApplicationShutdown_Dispose_StopsTheWorker()
    {
        using var launcher = WorkerTestEnvironment.CreateLauncher();
        var manager = Manager(launcher);
        await manager.StartAsync(CancellationToken.None);
        var pid = manager.ProcessId!.Value;

        await manager.DisposeAsync();

        Assert.True(await ProcessAssert.WaitUntilAsync(() => ProcessAssert.IsGone(pid), TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(CancellationToken.None));
    }

    [WorkerIntegrationFact]
    public async Task ClosingTheLauncher_KillsAWorkerThatWasNeverStopped_JobObject()
    {
        var launcher = WorkerTestEnvironment.CreateLauncher();
        var manager = Manager(launcher);
        await manager.StartAsync(CancellationToken.None);
        var pid = manager.ProcessId!.Value;

        // Simulate the application dying: the OS closes the job handle without any graceful shutdown.
        launcher.Dispose();

        Assert.True(await ProcessAssert.WaitUntilAsync(() => ProcessAssert.IsGone(pid), TimeSpan.FromSeconds(10)), "KILL_ON_JOB_CLOSE must end the worker.");
        await manager.DisposeAsync();
    }

    [WorkerIntegrationFact]
    public async Task SilentUnauthorizedClient_IsRejected_AndTheRealWorkerStillConnects()
    {
        using var real = WorkerTestEnvironment.CreateLauncher();
        var launcher = new ScriptedLauncher((attempt, spec) =>
        {
            // A same-user process that grabs the single pipe instance first and never speaks.
            _ = Task.Run(async () =>
            {
                await using var rogue = new System.IO.Pipes.NamedPipeClientStream(".", spec.PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await rogue.ConnectAsync(10_000);
                await Task.Delay(TimeSpan.FromSeconds(3));
            });
            Thread.Sleep(300);
            return real.Launch(spec);
        });
        var options = FastOptions() with { InitializeTimeout = TimeSpan.FromMilliseconds(800) };
        await using var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher, options);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.Equal(WorkerComponentState.Connected, (await manager.GetHealthAsync(CancellationToken.None)).Ipc);
    }

    [WorkerIntegrationFact]
    public async Task ClientWithTheWrongToken_IsRejected_AndTheRealWorkerStillConnects()
    {
        using var real = WorkerTestEnvironment.CreateLauncher();
        var launcher = new ScriptedLauncher((attempt, spec) =>
        {
            _ = Task.Run(async () =>
            {
                await using var rogue = new System.IO.Pipes.NamedPipeClientStream(".", spec.PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await rogue.ConnectAsync(10_000);
                using var frame = await WorkerFraming.ReadFrameAsync(rogue, CancellationToken.None);
                var request = WorkerMessageSerializer.Deserialize(frame!.Payload);
                // A convincing initialize.result, but without the session token the application generated.
                var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { workerVersion = "evil", protocolVersion = "1.0", sessionToken = "0000" });
                var response = new WorkerMessageEnvelope("1.0", request.RequestId, "worker.initialize.result", payload, DateTimeOffset.UtcNow);
                await WorkerFraming.WriteFrameAsync(rogue, WorkerMessageSerializer.Serialize(response), CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(3));
            });
            Thread.Sleep(300);
            return real.Launch(spec);
        });
        await using var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher, FastOptions());

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        var health = await manager.GetHealthAsync(CancellationToken.None);
        Assert.Equal(manager.ProcessId, health.ProcessId);
        Assert.NotEqual("evil", health.WorkerVersion);
    }

    [Fact]
    public async Task MissingPython_ReportsUnavailable_WithAnActionableMessage()
    {
        using var launcher = new PythonWorkerProcessLauncher(new PythonWorkerLaunchOptions { InterpreterPath = @"C:\definitely\not\here\python.exe", WorkerDirectory = null });
        await using var manager = new WorkerProcessManager(new NamedPipeWorkerListenerFactory(), launcher, FastOptions());

        var ex = await Assert.ThrowsAsync<WorkerException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(WorkerErrorKind.PythonNotFound, ex.Kind);
        Assert.Equal(WorkerStatus.Unavailable, manager.Status);
        Assert.Contains("python.exe", manager.LastError, StringComparison.OrdinalIgnoreCase);
    }
}
