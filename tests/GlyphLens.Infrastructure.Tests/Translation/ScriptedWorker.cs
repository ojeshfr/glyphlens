using System.IO.Pipes;
using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Infrastructure.Tests.IPC;

namespace GlyphLens.Infrastructure.Tests.Translation;

/// <summary>What a scripted worker answers: a message type (for example <c>translation.translate.result</c> or <c>error</c>) and its payload.</summary>
internal sealed record ScriptedReply(string Type, object? Payload);

/// <summary>
/// A pretend worker that speaks the real protocol over a real pipe (handshake, health, shutdown) and hands every other request
/// to a script. The script returns the reply, or <see langword="null"/> to stay silent (to provoke a timeout); it may also crash
/// the process.
/// </summary>
internal sealed class ScriptedLauncher(Func<WorkerMessageEnvelope, FakeWorkerProcess, Task<ScriptedReply?>> script) : IWorkerProcessLauncher
{
    private readonly List<FakeWorkerProcess> _processes = [];

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

    public IWorkerProcess Launch(WorkerLaunchSpec spec)
    {
        var process = new FakeWorkerProcess();
        lock (_processes)
        {
            _processes.Add(process);
        }

        _ = Task.Run(() => RunAsync(spec, process));
        return process;
    }

    private async Task RunAsync(WorkerLaunchSpec spec, FakeWorkerProcess process)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", spec.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(10_000, process.KilledToken);
            process.CrashHook = client.Dispose;

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
                        await ReplyAsync(client, request, "worker.initialize.result", new
                        {
                            workerVersion = "0.2.0-test",
                            protocolVersion = "1.0",
                            python = "3.12.0",
                            sessionToken = spec.SessionToken,
                        });
                        break;

                    case WorkerMessageTypes.WorkerHealth:
                        await ReplyAsync(client, request, "worker.health.result", FakeWorkerBehavior.TruthfulHealth(process.Id));
                        break;

                    case WorkerMessageTypes.WorkerShutdown:
                        await ReplyAsync(client, request, "worker.shutdown.result", new { status = "stopping" });
                        process.Exit(0);
                        return;

                    default:
                        if (await script(request, process) is { } reply)
                        {
                            await ReplyAsync(client, request, reply.Type, reply.Payload);
                        }

                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
            process.Exit(process.Exited.IsCompleted ? 0 : 1);
        }
    }

    private static Task ReplyAsync(Stream client, WorkerMessageEnvelope request, string type, object? payload)
    {
        var element = payload is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(payload, WorkerMessageSerializer.Options);
        var envelope = new WorkerMessageEnvelope("1.0", request.RequestId, type, element, DateTimeOffset.UtcNow);
        return WorkerFraming.WriteFrameAsync(client, WorkerMessageSerializer.Serialize(envelope), CancellationToken.None).AsTask();
    }
}
