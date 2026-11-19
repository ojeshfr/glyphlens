using System.IO.Pipes;
using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

/// <summary>How a fake worker behaves. Defaults describe a well-behaved worker.</summary>
internal sealed record FakeWorkerBehavior
{
    /// <summary>Exit immediately with this code, without ever connecting.</summary>
    public int? ExitImmediately { get; init; }

    /// <summary>Stay alive but never connect to the pipe.</summary>
    public bool NeverConnect { get; init; }

    /// <summary>Echo a token other than the session token in <c>worker.initialize.result</c>.</summary>
    public bool WrongToken { get; init; }

    /// <summary>Report this protocol version in <c>worker.initialize.result</c>.</summary>
    public string ProtocolVersion { get; init; } = "1.0";

    /// <summary>Answer <c>worker.initialize</c> with an error response.</summary>
    public string? RejectInitializeWith { get; init; }

    /// <summary>Connect but never answer <c>worker.initialize</c>.</summary>
    public bool IgnoreInitialize { get; init; }

    /// <summary>Answer the first health request (the start-up check) but ignore all later ones.</summary>
    public bool IgnoreLaterHealthRequests { get; init; }

    /// <summary>Acknowledge <c>worker.shutdown</c> but keep running.</summary>
    public bool IgnoreShutdown { get; init; }

    /// <summary>Do not even acknowledge <c>worker.shutdown</c>.</summary>
    public bool NoShutdownAck { get; init; }

    /// <summary>The health an engine-less build reports.</summary>
    public object Health { get; init; } = TruthfulHealth(0);

    public static object TruthfulHealth(int pid) => new
    {
        process = "ready",
        ipc = "connected",
        protocol = "initialized",
        engines = new { ocr = "not_implemented", languageDetection = "not_implemented", translation = "not_implemented" },
        models = "not_installed",
        workerVersion = "0.2.0-test",
        protocolVersion = "1.0",
        python = "3.12.0",
        pid,
    };
}

/// <summary>A pretend worker process: exits when told to, or when killed.</summary>
internal sealed class FakeWorkerProcess : IWorkerProcess
{
    private static int _nextId = 50_000;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _killed = new();

    public int Id { get; } = Interlocked.Increment(ref _nextId);

    public Task<int> Exited => _exited.Task;

    public IReadOnlyList<string> RecentErrorOutput { get; set; } = [];

    public bool WasKilled { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Cancelled when the process is killed (or exits); the fake worker loop stops on it.</summary>
    public CancellationToken KilledToken => _killed.Token;

    /// <summary>Simulate a crash: drop the pipe and exit with the given code.</summary>
    public Action? CrashHook { get; set; }

    public void Crash(int code = 1)
    {
        // Record the exit code first: dropping the pipe makes the fake's own loop exit with a different code.
        _exited.TrySetResult(code);
        CrashHook?.Invoke();
        Exit(code);
    }

    public void Exit(int code)
    {
        _exited.TrySetResult(code);
        try
        {
            _killed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    public void Kill()
    {
        WasKilled = true;
        Exit(-1);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A scriptable launcher that starts a fake worker (running the real protocol over a real pipe) per launch.</summary>
internal sealed class FakeLauncher(Func<int, FakeWorkerBehavior> behaviorFor) : IWorkerProcessLauncher
{
    private readonly List<FakeWorkerProcess> _processes = [];

    public FakeLauncher()
        : this(_ => new FakeWorkerBehavior())
    {
    }

    public IReadOnlyList<FakeWorkerProcess> Processes
    {
        get
        {
            lock (_processes)
            {
                return [.. _processes];
            }
        }
    }

    public int LaunchCount => Processes.Count;

    public Exception? ThrowOnLaunch { get; set; }

    public IWorkerProcess Launch(WorkerLaunchSpec spec)
    {
        if (ThrowOnLaunch is not null)
        {
            throw ThrowOnLaunch;
        }

        FakeWorkerProcess process;
        int attempt;
        lock (_processes)
        {
            process = new FakeWorkerProcess();
            _processes.Add(process);
            attempt = _processes.Count;
        }

        var behavior = behaviorFor(attempt);
        _ = Task.Run(() => FakeWorker.RunAsync(spec, behavior, process));
        return process;
    }
}

/// <summary>A pipe listener over a plain (un-ACL'd) named pipe, for tests that do not exercise Windows security.</summary>
internal sealed class TestPipeListenerFactory : IWorkerPipeListenerFactory
{
    public List<TestPipeListener> Listeners { get; } = [];

    public IWorkerPipeListener Create()
    {
        var listener = new TestPipeListener();
        lock (Listeners)
        {
            Listeners.Add(listener);
        }

        return listener;
    }
}

internal sealed class TestPipeListener : IWorkerPipeListener
{
    private readonly object _gate = new();
    private NamedPipeServerStream? _pending;

    public TestPipeListener()
    {
        PipeName = "glyphlens-test-" + Guid.NewGuid().ToString("N");
        _pending = NewInstance();
    }

    public string PipeName { get; }

    public bool IsDisposed { get; private set; }

    public int AcceptCount { get; private set; }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream server;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            AcceptCount++;
            server = _pending ?? NewInstance();
            _pending = server;
        }

        try
        {
            await server.WaitForConnectionAsync(cancellationToken);
        }
        catch
        {
            lock (_gate)
            {
                _pending = null;
            }

            await server.DisposeAsync();
            throw;
        }

        lock (_gate)
        {
            _pending = null;
        }

        return server;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            IsDisposed = true;
            _pending?.Dispose();
            _pending = null;
        }
    }

    private NamedPipeServerStream NewInstance() =>
        new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
}

/// <summary>The worker's side of the protocol, running in-process as a task.</summary>
internal static class FakeWorker
{
    public static async Task RunAsync(WorkerLaunchSpec spec, FakeWorkerBehavior behavior, FakeWorkerProcess process)
    {
        try
        {
            if (behavior.ExitImmediately is { } code)
            {
                process.Exit(code);
                return;
            }

            if (behavior.NeverConnect)
            {
                await Task.Delay(Timeout.Infinite, process.KilledToken);
                return;
            }

            await using var client = new NamedPipeClientStream(".", spec.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(10_000, process.KilledToken);
            process.CrashHook = client.Dispose;

            var healthRequests = 0;
            while (!process.KilledToken.IsCancellationRequested)
            {
                using var frame = await WorkerFraming.ReadFrameAsync(client, process.KilledToken);
                if (frame is null)
                {
                    process.Exit(5);
                    return;
                }

                var request = WorkerMessageSerializer.Deserialize(frame.Payload);
                switch (request.MessageType)
                {
                    case WorkerMessageTypes.WorkerInitialize:
                        if (behavior.IgnoreInitialize)
                        {
                            break;
                        }

                        if (behavior.RejectInitializeWith is { } rejection)
                        {
                            await Reply(client, request, "error", new { code = rejection, message = "refused", retryable = false });
                            break;
                        }

                        await Reply(client, request, "worker.initialize.result", new
                        {
                            workerVersion = "0.2.0-test",
                            protocolVersion = behavior.ProtocolVersion,
                            python = "3.12.0",
                            sessionToken = behavior.WrongToken ? "not-the-token" : spec.SessionToken,
                        });
                        break;

                    case WorkerMessageTypes.WorkerHealth:
                        healthRequests++;
                        if (behavior.IgnoreLaterHealthRequests && healthRequests > 1)
                        {
                            break;
                        }

                        await Reply(client, request, "worker.health.result", behavior.Health is { } h && h.GetType().GetProperty("pid")?.GetValue(h) is 0
                            ? FakeWorkerBehavior.TruthfulHealth(process.Id)
                            : behavior.Health);
                        break;

                    case WorkerMessageTypes.WorkerShutdown:
                        if (behavior.NoShutdownAck)
                        {
                            break;
                        }

                        await Reply(client, request, "worker.shutdown.result", new { status = "stopping" });
                        if (!behavior.IgnoreShutdown)
                        {
                            process.Exit(0);
                            return;
                        }

                        break;

                    default:
                        await Reply(client, request, "error", new { code = "engine_unavailable", message = "not implemented", retryable = false });
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
            process.Exit(process.Exited.IsCompleted ? 0 : 1);
        }
    }

    private static Task Reply(Stream client, WorkerMessageEnvelope request, string type, object payload)
    {
        var envelope = new WorkerMessageEnvelope("1.0", request.RequestId, type, JsonSerializer.SerializeToElement(payload, WorkerMessageSerializer.Options), DateTimeOffset.UtcNow);
        return WorkerFraming.WriteFrameAsync(client, WorkerMessageSerializer.Serialize(envelope), CancellationToken.None).AsTask();
    }
}
