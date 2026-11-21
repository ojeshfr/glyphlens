using System.IO.Pipes;
using System.Text;
using GlyphLens.Core.Abstractions;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Windows.Worker;

namespace GlyphLens.Windows.Tests.Worker;

/// <summary>
/// Hostile and malformed input sent to the REAL Python worker over a real pipe, with the test acting as the application's
/// pipe server. Verifies responses, exit codes and that the worker survives or exits as documented in
/// docs/IPC-PROTOCOL.md. No OCR models or network are involved.
/// </summary>
public sealed class RealWorkerProtocolTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private const string Token = "test-session-token-0123456789";

    /// <summary>An application-side pipe server plus a real worker connected to it.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly PythonWorkerProcessLauncher _launcher;

        private Session(NamedPipeServerStream pipe, IWorkerProcess process, PythonWorkerProcessLauncher launcher)
        {
            Pipe = pipe;
            Process = process;
            _launcher = launcher;
        }

        public NamedPipeServerStream Pipe { get; }

        public IWorkerProcess Process { get; }

        public static async Task<Session> StartAsync()
        {
            var name = "glyphlens-" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var launcher = WorkerTestEnvironment.CreateLauncher();
            var process = launcher.Launch(new WorkerLaunchSpec(name, Token));
            using var cts = new CancellationTokenSource(Long);
            await pipe.WaitForConnectionAsync(cts.Token);
            return new Session(pipe, process, launcher);
        }

        public async Task<WorkerMessageEnvelope> RequestAsync(string type, object? payload = null, string? requestId = null)
        {
            requestId ??= Guid.NewGuid().ToString("N");
            await Wire(requestId, type, payload);
            return await ReadAsync();
        }

        public Task Wire(string requestId, string type, object? payload = null)
        {
            var element = payload is null ? (System.Text.Json.JsonElement?)null : System.Text.Json.JsonSerializer.SerializeToElement(payload, WorkerMessageSerializer.Options);
            var envelope = new WorkerMessageEnvelope("1.0", requestId, type, element, DateTimeOffset.UtcNow);
            return WorkerFraming.WriteFrameAsync(Pipe, WorkerMessageSerializer.Serialize(envelope), CancellationToken.None).AsTask();
        }

        public async Task<WorkerMessageEnvelope> ReadAsync()
        {
            using var cts = new CancellationTokenSource(Long);
            using var frame = await WorkerFraming.ReadFrameAsync(Pipe, cts.Token) ?? throw new EndOfStreamException("The worker closed the pipe.");
            return WorkerMessageSerializer.Deserialize(frame.Payload);
        }

        public Task SendRawFrameAsync(byte[] payload) => WorkerFraming.WriteFrameAsync(Pipe, payload, CancellationToken.None).AsTask();

        public async Task SendRawBytesAsync(byte[] bytes)
        {
            await Pipe.WriteAsync(bytes);
            await Pipe.FlushAsync();
        }

        public Task<WorkerMessageEnvelope> InitializeAsync() => RequestAsync("worker.initialize", new { clientProtocolVersion = "1.0" });

        public async Task<int> WaitForExitCodeAsync() => await Process.Exited.WaitAsync(Long);

        public async ValueTask DisposeAsync()
        {
            await Pipe.DisposeAsync();
            Process.Kill();
            Process.Dispose();
            _launcher.Dispose();
        }
    }

    private static string ErrorCode(WorkerMessageEnvelope envelope)
    {
        Assert.Equal("error", envelope.MessageType);
        return envelope.Payload!.Value.GetProperty("code").GetString()!;
    }

    [WorkerIntegrationFact]
    public async Task Handshake_HealthAndShutdown_WorkAsDocumented()
    {
        await using var session = await Session.StartAsync();

        var init = await session.InitializeAsync();
        var health = await session.RequestAsync("worker.health", requestId: "health-77");
        var shutdown = await session.RequestAsync("worker.shutdown");

        Assert.Equal("worker.initialize.result", init.MessageType);
        Assert.Equal(Token, init.Payload!.Value.GetProperty("sessionToken").GetString());
        Assert.Equal("health-77", health.RequestId);
        Assert.Equal("ready", health.Payload!.Value.GetProperty("process").GetString());
        Assert.Equal("connected", health.Payload!.Value.GetProperty("ipc").GetString());
        Assert.Equal("initialized", health.Payload!.Value.GetProperty("protocol").GetString());
        Assert.Equal("worker.shutdown.result", shutdown.MessageType);
        Assert.Equal(0, await session.WaitForExitCodeAsync());
    }

    [WorkerIntegrationFact]
    public async Task RequestsBeforeInitialize_AreRefused()
    {
        await using var session = await Session.StartAsync();

        Assert.Equal("not_initialized", ErrorCode(await session.RequestAsync("worker.health")));
        Assert.Equal("worker.initialize.result", (await session.InitializeAsync()).MessageType); // still serving
    }

    [WorkerIntegrationFact]
    public async Task UnknownAndUnimplementedMessageTypes_GetErrors_NeverFakeResults()
    {
        await using var session = await Session.StartAsync();
        await session.InitializeAsync();

        Assert.Equal("unknown_message_type", ErrorCode(await session.RequestAsync("worker.reboot")));
        Assert.Equal("engine_unavailable", ErrorCode(await session.RequestAsync("translation.cancel", new { requestId = "x" })));

        // The engine messages validate their input: a malformed request is refused, never answered with an invented result.
        Assert.Equal("invalid_request", ErrorCode(await session.RequestAsync("ocr.detect", new { width = 1 })));
        Assert.Equal("invalid_request", ErrorCode(await session.RequestAsync("translation.translate", new { text = "x" })));
        Assert.Equal("invalid_request", ErrorCode(await session.RequestAsync("language.detect", new { text = "x" })));

        // models.list reports what is installed (nothing is not an error).
        Assert.Equal("models.list.result", (await session.RequestAsync("models.list")).MessageType);
    }

    [WorkerIntegrationFact]
    public async Task InvalidJson_GetsAnError_AndTheWorkerKeepsServing()
    {
        await using var session = await Session.StartAsync();

        await session.SendRawFrameAsync("{this is not json"u8.ToArray());
        var error = await session.ReadAsync();

        Assert.Equal("invalid_message", ErrorCode(error));
        Assert.Equal("unknown", error.RequestId);
        Assert.Equal("worker.initialize.result", (await session.InitializeAsync()).MessageType);
    }

    [WorkerIntegrationFact]
    public async Task InvalidUtf8_GetsAnError_AndTheWorkerKeepsServing()
    {
        await using var session = await Session.StartAsync();

        await session.SendRawFrameAsync([0xFF, 0xFE, 0x00, 0x7B]);

        Assert.Equal("invalid_message", ErrorCode(await session.ReadAsync()));
        Assert.Equal("worker.initialize.result", (await session.InitializeAsync()).MessageType);
    }

    [WorkerIntegrationFact]
    public async Task AnUnsupportedProtocolVersion_IsRefusedWithItsOwnCode()
    {
        await using var session = await Session.StartAsync();

        await session.SendRawFrameAsync(Encoding.UTF8.GetBytes(
            """{"protocolVersion":"2.0","requestId":"v2-req","messageType":"worker.health","payload":null,"timestamp":"2026-01-01T00:00:00Z"}"""));
        var error = await session.ReadAsync();

        Assert.Equal("unsupported_protocol_version", ErrorCode(error));
        Assert.Equal("v2-req", error.RequestId);
    }

    [WorkerIntegrationFact]
    public async Task AnIncompatibleClientVersionInInitialize_IsRefused()
    {
        await using var session = await Session.StartAsync();

        var response = await session.RequestAsync("worker.initialize", new { clientProtocolVersion = "2.0" });

        Assert.Equal("unsupported_protocol_version", ErrorCode(response));
    }

    [WorkerIntegrationFact]
    public async Task TooManyInvalidMessagesInARow_AreFatal_WithExitCode4()
    {
        await using var session = await Session.StartAsync();

        for (var i = 0; i < 5; i++)
        {
            await session.SendRawFrameAsync("nope"u8.ToArray());
            Assert.Equal("invalid_message", ErrorCode(await session.ReadAsync()));
        }

        Assert.Equal(4, await session.WaitForExitCodeAsync());
    }

    [WorkerIntegrationFact]
    public async Task AnOversizedFrame_IsRejectedBeforeItsPayloadIsRead_AndIsFatal()
    {
        await using var session = await Session.StartAsync();

        // Announce 16 MiB + 1 and send no payload at all: the worker must refuse on the header alone.
        await session.SendRawBytesAsync(BitConverter.GetBytes((uint)WorkerFraming.MaxFrameBytes + 1));
        var error = await session.ReadAsync();

        Assert.Equal("payload_too_large", ErrorCode(error));
        Assert.Equal(4, await session.WaitForExitCodeAsync());
    }

    [WorkerIntegrationFact]
    public async Task TheLargestPossibleLengthField_IsRejected_WithoutAllocation()
    {
        await using var session = await Session.StartAsync();

        await session.SendRawBytesAsync(BitConverter.GetBytes(uint.MaxValue));

        Assert.Equal("payload_too_large", ErrorCode(await session.ReadAsync()));
        Assert.Equal(4, await session.WaitForExitCodeAsync());
    }

    [WorkerIntegrationFact]
    public async Task AZeroLengthFrame_IsFatal()
    {
        await using var session = await Session.StartAsync();

        await session.SendRawBytesAsync(new byte[4]);
        // The worker sends a best-effort error before exiting; read it so its write cannot block on an unread pipe.
        Assert.Equal("error", (await session.ReadAsync()).MessageType);

        Assert.Equal(4, await session.WaitForExitCodeAsync());
    }

    [WorkerIntegrationFact]
    public async Task AFrameCutShort_ByTheApplicationClosingThePipe_IsFatal()
    {
        await using var session = await Session.StartAsync();

        await session.SendRawBytesAsync([.. BitConverter.GetBytes(100u), .. "short"u8.ToArray()]);
        await session.Pipe.DisposeAsync();

        Assert.Equal(4, await session.WaitForExitCodeAsync());
    }

    [WorkerIntegrationFact]
    public async Task TheApplicationDisappearing_MakesTheWorkerExitOnItsOwn_WithPipeLostCode()
    {
        await using var session = await Session.StartAsync();
        await session.InitializeAsync();

        await session.Pipe.DisposeAsync(); // no shutdown request: as if GlyphLens was killed

        Assert.Equal(5, await session.WaitForExitCodeAsync());
    }

    [WorkerIntegrationFact]
    public async Task ALargeMessage_CrossesTheProcessBoundary_WithPartialReads()
    {
        await using var session = await Session.StartAsync();
        await session.InitializeAsync();

        // About 8 MiB: far larger than any pipe buffer, so both sides must loop over partial reads and writes.
        var big = new string('x', 8 * 1024 * 1024);
        var response = await session.RequestAsync("worker.health", new { padding = big }, requestId: "big-1");

        Assert.Equal("worker.health.result", response.MessageType);
        Assert.Equal("big-1", response.RequestId);
    }

    [WorkerIntegrationFact]
    public async Task AMessageJustUnderTheLimit_IsAccepted()
    {
        await using var session = await Session.StartAsync();
        await session.InitializeAsync();

        var padding = new string('y', WorkerFraming.MaxFrameBytes - 4096);
        var response = await session.RequestAsync("worker.health", new { padding });

        Assert.Equal("worker.health.result", response.MessageType);
    }

    [WorkerIntegrationFact]
    public async Task ManySequentialRequests_AreEachAnsweredWithTheirOwnId()
    {
        await using var session = await Session.StartAsync();
        await session.InitializeAsync();

        for (var i = 0; i < 200; i++)
        {
            var response = await session.RequestAsync("worker.health", requestId: "r-" + i);
            Assert.Equal("r-" + i, response.RequestId);
        }
    }

    [WorkerIntegrationFact]
    public async Task TheWorkersLogs_NeverContainPayloadContent()
    {
        await using var session = await Session.StartAsync();
        await session.InitializeAsync();
        const string Secret = "TOP-SECRET-SCREEN-TEXT-12345";

        var response = await session.RequestAsync("translation.translate", new { text = Secret }, requestId: "t-9");
        await session.RequestAsync("worker.shutdown");
        await session.WaitForExitCodeAsync();

        Assert.Equal("invalid_request", ErrorCode(response));
        var logs = string.Join("\n", session.Process.RecentErrorOutput);
        Assert.Contains("req=t-9", logs, StringComparison.Ordinal);
        Assert.Contains("type=translation.translate", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, response.Payload!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, logs, StringComparison.Ordinal); // the session token is never logged either
    }

    // ---- startup validation (no pipe needed) ----

    [WorkerIntegrationFact]
    public async Task AnArbitraryPipeName_IsRefusedWithoutConnecting()
    {
        using var process = WorkerTestEnvironment.StartPython("-E", "-m", "glyphlens_worker", "--pipe", @"\\.\pipe\some-other-pipe");

        // Run from the worker directory so the package is importable.
        process.Kill();
        await process.WaitForExitAsync();
        var info = new System.Diagnostics.ProcessStartInfo(WorkerTestEnvironment.Python.Path)
        {
            WorkingDirectory = WorkerTestEnvironment.WorkerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-E", "-m", "glyphlens_worker", "--pipe", @"\\.\pipe\some-other-pipe" })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment[PythonWorkerProcessLauncher.TokenEnvironmentVariable] = Token;
        using var worker = System.Diagnostics.Process.Start(info)!;
        var stderr = await worker.StandardError.ReadToEndAsync();
        await worker.WaitForExitAsync();

        Assert.Equal(2, worker.ExitCode);
        Assert.Contains("not a GlyphLens session pipe name", stderr, StringComparison.Ordinal);
    }

    [WorkerIntegrationFact]
    public async Task AMissingSessionToken_IsRefused_WithItsOwnExitCode()
    {
        var info = new System.Diagnostics.ProcessStartInfo(WorkerTestEnvironment.Python.Path)
        {
            WorkingDirectory = WorkerTestEnvironment.WorkerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-E", "-m", "glyphlens_worker", "--pipe", "glyphlens-" + Guid.NewGuid().ToString("N") })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(PythonWorkerProcessLauncher.TokenEnvironmentVariable);
        using var worker = System.Diagnostics.Process.Start(info)!;
        await worker.StandardError.ReadToEndAsync();
        await worker.WaitForExitAsync();

        Assert.Equal(6, worker.ExitCode);
    }
}
