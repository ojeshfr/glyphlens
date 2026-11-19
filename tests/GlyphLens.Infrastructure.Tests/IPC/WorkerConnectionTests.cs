using System.Text.Json;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

/// <summary>
/// Request/response behaviour of <see cref="WorkerConnection"/> over a real (loopback) named pipe, with the test
/// playing the worker.
/// </summary>
public class WorkerConnectionTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private static async Task<(PipePair Pair, WorkerConnection Connection)> OpenAsync(RecordingLog? log = null, int maxPending = 32)
    {
        var pair = await PipePair.CreateAsync();
        var connection = new WorkerConnection(pair.Server, log, maxPending);
        connection.Start();
        return (pair, connection);
    }

    [Fact]
    public async Task Request_ReturnsTheCorrelatedResponse()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);

            var received = await Wire.ReadAsync(pair.Client);
            Assert.Equal("worker.health", received.MessageType);
            Assert.Equal("1.0", received.ProtocolVersion);
            Assert.False(string.IsNullOrWhiteSpace(received.RequestId));
            await Wire.WriteAsync(pair.Client, received.RequestId, "worker.health.result", new { process = "ready" });

            var response = await request;
            Assert.Equal(received.RequestId, response.RequestId);
            Assert.Equal("ready", response.Payload!.Value.GetProperty("process").GetString());
        }
    }

    [Fact]
    public async Task Request_SerializesThePayloadWithCamelCaseNames()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerInitialize, new { ClientProtocolVersion = "1.0" }, Long, CancellationToken.None);

            var received = await Wire.ReadAsync(pair.Client);

            Assert.Equal("1.0", received.Payload!.Value.GetProperty("clientProtocolVersion").GetString());
            await Wire.WriteAsync(pair.Client, received.RequestId, "worker.initialize.result");
            await request;
        }
    }

    [Fact]
    public async Task OverlappingRequests_AreMatchedByIdEvenWhenAnsweredOutOfOrder()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var first = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var second = connection.RequestAsync(WorkerMessageTypes.ModelsList, null, Long, CancellationToken.None);
            var a = await Wire.ReadAsync(pair.Client);
            var b = await Wire.ReadAsync(pair.Client);

            // Answer the second request first.
            await Wire.WriteAsync(pair.Client, b.RequestId, b.MessageType + ".result", new { which = "second" });
            await Wire.WriteAsync(pair.Client, a.RequestId, a.MessageType + ".result", new { which = "first" });

            Assert.Equal("first", (await first).Payload!.Value.GetProperty("which").GetString());
            Assert.Equal("second", (await second).Payload!.Value.GetProperty("which").GetString());
        }
    }

    [Fact]
    public async Task FiftyConcurrentRequests_AllCompleteWithTheirOwnResponses()
    {
        var (pair, connection) = await OpenAsync(maxPending: 64);
        await using (pair)
        await using (connection)
        {
            var requests = Enumerable.Range(0, 50)
                .Select(i => connection.RequestAsync(WorkerMessageTypes.WorkerHealth, new { n = i }, Long, CancellationToken.None))
                .ToList();

            var received = new List<WorkerMessageEnvelope>();
            for (var i = 0; i < 50; i++)
            {
                received.Add(await Wire.ReadAsync(pair.Client));
            }

            foreach (var message in received.AsEnumerable().Reverse())
            {
                await Wire.WriteAsync(pair.Client, message.RequestId, "worker.health.result", new { n = message.Payload!.Value.GetProperty("n").GetInt32() });
            }

            var results = await Task.WhenAll(requests);
            Assert.Equal(Enumerable.Range(0, 50), results.Select(r => r.Payload!.Value.GetProperty("n").GetInt32()));
        }
    }

    [Fact]
    public async Task ErrorResponse_BecomesARejectedException_WithTheCode()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.OcrDetect, null, Long, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);
            await Wire.WriteAsync(pair.Client, received.RequestId, "error", new { code = "engine_unavailable", message = "not implemented", retryable = false });

            var ex = await Assert.ThrowsAsync<WorkerException>(() => request);

            Assert.Equal(WorkerErrorKind.Rejected, ex.Kind);
            Assert.Equal("engine_unavailable", ex.ErrorCode);
            Assert.Contains("not implemented", ex.Message, StringComparison.Ordinal);
            Assert.True(connection.IsOpen); // an error response is not a connection failure
        }
    }

    [Fact]
    public async Task ErrorText_IsSanitisedAndTruncated()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);
            await Wire.WriteAsync(pair.Client, received.RequestId, "error", new { code = "x", message = "line1\r\nline2\u001b[31m" + new string('A', 5000) });

            var ex = await Assert.ThrowsAsync<WorkerException>(() => request);

            Assert.DoesNotContain('\n', ex.Message);
            Assert.DoesNotContain('\u001b', ex.Message);
            Assert.True(ex.Message.Length < 400);
        }
    }

    [Fact]
    public async Task NoAnswer_TimesOut_AndALateAnswerIsIgnored()
    {
        var log = new RecordingLog();
        var (pair, connection) = await OpenAsync(log);
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Short, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);

            var ex = await Assert.ThrowsAsync<WorkerException>(() => request);
            await Wire.WriteAsync(pair.Client, received.RequestId, "worker.health.result"); // too late

            Assert.Equal(WorkerErrorKind.RequestTimeout, ex.Kind);
            Assert.Equal(0, connection.PendingRequestCount);
            Assert.True(connection.IsOpen);
            // The connection still works afterwards.
            var next = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var again = await Wire.ReadAsync(pair.Client);
            await Wire.WriteAsync(pair.Client, again.RequestId, "worker.health.result");
            await next;
            Assert.Contains(log.Requests, e => e.Outcome == "timeout");
        }
    }

    [Fact]
    public async Task Cancellation_StopsWaiting_AndLeavesNothingPending()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            using var cts = new CancellationTokenSource();
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, cts.Token);
            await Wire.ReadAsync(pair.Client);

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(0, connection.PendingRequestCount);
            Assert.True(connection.IsOpen);
        }
    }

    [Fact]
    public async Task PeerClosing_FailsPendingRequests_AndReportsPeerClosed()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            await Wire.ReadAsync(pair.Client);

            await pair.Client.DisposeAsync();

            var ex = await Assert.ThrowsAsync<WorkerException>(() => request);
            Assert.Equal(WorkerErrorKind.ConnectionLost, ex.Kind);
            var closed = await connection.Closed.WaitAsync(Long);
            Assert.Equal(WorkerConnectionCloseReason.PeerClosed, closed.Reason);
            Assert.False(connection.IsOpen);
        }
    }

    [Fact]
    public async Task RequestAfterClose_FailsWithNotConnected()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            await pair.Client.DisposeAsync();
            await connection.Closed.WaitAsync(Long);

            var ex = await Assert.ThrowsAsync<WorkerException>(() => connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None));

            Assert.Equal(WorkerErrorKind.NotConnected, ex.Kind);
        }
    }

    [Theory]
    [InlineData("zero-length")]
    [InlineData("oversized")]
    [InlineData("invalid-json")]
    [InlineData("invalid-utf8")]
    [InlineData("not-an-envelope")]
    [InlineData("wrong-version")]
    [InlineData("truncated")]
    public async Task MalformedInput_ClosesTheConnection_AndFailsPendingRequests(string kind)
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);

            switch (kind)
            {
                case "zero-length":
                    await Wire.WriteRawBytesAsync(pair.Client, Wire.Header(0));
                    break;
                case "oversized":
                    await Wire.WriteRawBytesAsync(pair.Client, Wire.Header((uint)WorkerFraming.MaxFrameBytes + 1));
                    break;
                case "invalid-json":
                    await Wire.WriteRawFrameAsync(pair.Client, "{nope"u8.ToArray());
                    break;
                case "invalid-utf8":
                    await Wire.WriteRawFrameAsync(pair.Client, [0xFF, 0xFE, 0x00, 0x7B]);
                    break;
                case "not-an-envelope":
                    await Wire.WriteRawFrameAsync(pair.Client, "[1,2,3]"u8.ToArray());
                    break;
                case "wrong-version":
                    await Wire.WriteRawFrameAsync(pair.Client, System.Text.Encoding.UTF8.GetBytes(
                        $$"""{"protocolVersion":"9.0","requestId":"{{received.RequestId}}","messageType":"worker.health.result","payload":null,"timestamp":"2026-01-01T00:00:00Z"}"""));
                    break;
                case "truncated":
                    await Wire.WriteRawBytesAsync(pair.Client, [.. Wire.Header(100), .. "short"u8.ToArray()]);
                    await pair.Client.DisposeAsync();
                    break;
            }

            var ex = await Assert.ThrowsAsync<WorkerException>(() => request);
            var closed = await connection.Closed.WaitAsync(Long);

            Assert.False(connection.IsOpen);
            Assert.Contains(ex.Kind, new[] { WorkerErrorKind.ProtocolViolation, WorkerErrorKind.ConnectionLost });
            Assert.Contains(closed.Reason, new[] { WorkerConnectionCloseReason.ProtocolViolation, WorkerConnectionCloseReason.PeerClosed });
            if (kind != "truncated")
            {
                Assert.Equal(WorkerConnectionCloseReason.ProtocolViolation, closed.Reason);
            }
        }
    }

    [Fact]
    public async Task ProtocolViolationDetails_NeverContainPayloadContent()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            await Wire.WriteRawFrameAsync(pair.Client, "{\"secret\":\"TOP-SECRET-SCREEN-TEXT\""u8.ToArray()); // broken JSON containing "content"

            var closed = await connection.Closed.WaitAsync(Long);

            Assert.Equal(WorkerConnectionCloseReason.ProtocolViolation, closed.Reason);
            Assert.DoesNotContain("TOP-SECRET", closed.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnsolicitedAndLateMessages_AreIgnored_WithoutClosingTheConnection()
    {
        var log = new RecordingLog();
        var (pair, connection) = await OpenAsync(log);
        await using (pair)
        await using (connection)
        {
            await Wire.WriteAsync(pair.Client, "nobody-asked", "worker.health.result");

            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);
            await Wire.WriteAsync(pair.Client, received.RequestId, "worker.health.result");
            await request;

            Assert.True(connection.IsOpen);
            Assert.Contains(log.Events, e => e.Contains("unsolicited", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task AResponseOfTheWrongType_FailsThatRequestOnly()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);
            await Wire.WriteAsync(pair.Client, received.RequestId, "translation.translate.result");

            var ex = await Assert.ThrowsAsync<WorkerException>(() => request);

            Assert.Equal(WorkerErrorKind.ProtocolViolation, ex.Kind);
            Assert.True(connection.IsOpen);
        }
    }

    [Fact]
    public async Task TooManyOutstandingRequests_AreRejectedInsteadOfQueued()
    {
        var (pair, connection) = await OpenAsync(maxPending: 2);
        await using (pair)
        await using (connection)
        {
            var first = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var second = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<WorkerException>(() => connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None));

            Assert.Equal(WorkerErrorKind.TooManyRequests, ex.Kind);
            Assert.Equal(2, connection.PendingRequestCount);
            await connection.DisposeAsync();
            await Assert.ThrowsAsync<WorkerException>(() => first);
            await Assert.ThrowsAsync<WorkerException>(() => second);
        }
    }

    [Fact]
    public async Task ResponsesDeliveredOneByteAtATime_AreStillParsed()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);
            var envelope = new WorkerMessageEnvelope("1.0", received.RequestId, "worker.health.result", JsonSerializer.SerializeToElement(new { ok = true }), DateTimeOffset.UtcNow);
            var payload = WorkerMessageSerializer.Serialize(envelope);
            byte[] bytes = [.. Wire.Header((uint)payload.Length), .. payload];

            foreach (var b in bytes)
            {
                await pair.Client.WriteAsync(new[] { b });
                await pair.Client.FlushAsync();
            }

            Assert.True((await request).Payload!.Value.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task LargePayloads_RoundTrip()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            var big = new string('x', 1_000_000);
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, new { text = big }, Long, CancellationToken.None);

            var received = await Wire.ReadAsync(pair.Client);
            Assert.Equal(1_000_000, received.Payload!.Value.GetProperty("text").GetString()!.Length);
            await Wire.WriteAsync(pair.Client, received.RequestId, "worker.health.result", new { text = big });

            Assert.Equal(1_000_000, (await request).Payload!.Value.GetProperty("text").GetString()!.Length);
        }
    }

    [Fact]
    public async Task RequestLog_RecordsIdTypeOutcomeAndDuration_ButNeverThePayload()
    {
        var log = new RecordingLog();
        var (pair, connection) = await OpenAsync(log);
        await using (pair)
        await using (connection)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.TranslationTranslate, new { text = "TOP-SECRET-SCREEN-TEXT" }, Long, CancellationToken.None);
            var received = await Wire.ReadAsync(pair.Client);
            await Wire.WriteAsync(pair.Client, received.RequestId, "error", new { code = "engine_unavailable", message = "no" });
            await Assert.ThrowsAsync<WorkerException>(() => request);

            var entry = Assert.Single(log.Requests);
            Assert.Equal(received.RequestId, entry.RequestId);
            Assert.Equal("translation.translate", entry.MessageType);
            Assert.Equal("Rejected", entry.Outcome);
            Assert.Equal("engine_unavailable", entry.ErrorCode);
            Assert.True(entry.Duration >= TimeSpan.Zero);
            Assert.DoesNotContain("TOP-SECRET", entry.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Dispose_FailsPendingRequests_AndIsIdempotent()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        {
            var request = connection.RequestAsync(WorkerMessageTypes.WorkerHealth, null, Long, CancellationToken.None);
            await Wire.ReadAsync(pair.Client);

            await connection.DisposeAsync();
            await connection.DisposeAsync();

            await Assert.ThrowsAsync<WorkerException>(() => request);
            Assert.Equal(WorkerConnectionCloseReason.Disposed, (await connection.Closed).Reason);
        }
    }

    [Fact]
    public async Task Request_ValidatesArguments()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => connection.RequestAsync(" ", null, Long, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.RequestAsync("worker.health", null, TimeSpan.Zero, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Start_CannotBeCalledTwice()
    {
        var (pair, connection) = await OpenAsync();
        await using (pair)
        await using (connection)
        {
            Assert.Throws<InvalidOperationException>(connection.Start);
        }
    }
}
