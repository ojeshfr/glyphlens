using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Contracts;
using GlyphLens.Core.Models;

namespace GlyphLens.Infrastructure.IPC;

public enum WorkerConnectionCloseReason
{
    /// <summary>This side disposed the connection.</summary>
    Disposed = 1,

    /// <summary>The peer closed the pipe (cleanly or because it exited).</summary>
    PeerClosed,

    /// <summary>The peer sent data that violates the protocol.</summary>
    ProtocolViolation,

    /// <summary>The pipe broke while reading or writing.</summary>
    IoError,
}

/// <summary>Why and when a <see cref="WorkerConnection"/> ended. <see cref="Detail"/> never contains message content.</summary>
public sealed record WorkerConnectionClosed(WorkerConnectionCloseReason Reason, string? Detail);

/// <summary>
/// A request/response channel over a duplex stream carrying length-prefixed protocol envelopes.
/// </summary>
/// <remarks>
/// <para>
/// Requests are correlated to responses by <c>requestId</c>; responses can arrive in any order and requests can
/// overlap. A background loop reads frames; writers take a short lock only around the single frame write, never
/// around waiting for a response. At most <c>maxPendingRequests</c> requests wait for an answer, so the connection
/// cannot accumulate an unbounded backlog.
/// </para>
/// <para>
/// Any framing or envelope violation from the peer closes the connection (the byte stream cannot be trusted any
/// longer) and fails every waiting request. Nothing here logs or exposes message payloads.
/// </para>
/// </remarks>
public sealed class WorkerConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IWorkerLog _log;
    private readonly int _maxPendingRequests;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<WorkerConnectionClosed> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _readLoop = Task.CompletedTask;
    private int _closing;
    private int _started;

    public WorkerConnection(Stream stream, IWorkerLog? log = null, int maxPendingRequests = 32)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingRequests, 1);

        _stream = stream;
        _log = log ?? NullWorkerLog.Instance;
        _maxPendingRequests = maxPendingRequests;
    }

    /// <summary>Completes (never faults) when the connection ends, with the reason.</summary>
    public Task<WorkerConnectionClosed> Closed => _closed.Task;

    public bool IsOpen => Volatile.Read(ref _closing) == 0 && Volatile.Read(ref _started) == 1;

    public int PendingRequestCount => _pending.Count;

    /// <summary>Begins reading from the stream. Call once, before sending requests.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("The connection was already started.");
        }

        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Sends a request and waits for the matching response.
    /// </summary>
    /// <param name="messageType">A request type from <see cref="WorkerMessageTypes"/>.</param>
    /// <param name="payload">Optional payload, serialized with the shared protocol options.</param>
    /// <param name="timeout">How long to wait for the response.</param>
    /// <param name="cancellationToken">Cancels the wait (the request may still be processed by the worker).</param>
    /// <returns>The response envelope (<c>&lt;type&gt;.result</c>).</returns>
    /// <exception cref="WorkerException">Not connected, timed out, connection lost, protocol violation, or the worker returned an error.</exception>
    public async Task<WorkerMessageEnvelope> RequestAsync(string messageType, object? payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (!IsOpen)
        {
            throw new WorkerException(WorkerErrorKind.NotConnected, "The worker connection is not open.");
        }

        if (_pending.Count >= _maxPendingRequests)
        {
            throw new WorkerException(WorkerErrorKind.TooManyRequests, "Too many requests are waiting for the worker.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new PendingRequest(messageType, Stopwatch.GetTimestamp());
        _pending[requestId] = request;

        var outcome = "ok";
        string? errorCode = null;
        try
        {
            var envelope = new WorkerMessageEnvelope(
                ProtocolVersion.Current.ToString(),
                requestId,
                messageType,
                payload is null ? null : JsonSerializer.SerializeToElement(payload, WorkerMessageSerializer.Options),
                DateTimeOffset.UtcNow);
            var bytes = WorkerMessageSerializer.Serialize(envelope);

            await SendAsync(bytes, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await request.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = "timeout";
                throw new WorkerException(WorkerErrorKind.RequestTimeout, $"The worker did not answer '{messageType}' within {timeout.TotalSeconds:0.#} s.");
            }
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (WorkerException ex)
        {
            if (outcome == "ok")
            {
                outcome = ex.Kind.ToString();
            }

            errorCode = ex.ErrorCode;
            throw;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            _log.Request(new WorkerRequestLogEntry(requestId, messageType, Stopwatch.GetElapsedTime(request.StartTimestamp), outcome, errorCode));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close(WorkerConnectionCloseReason.Disposed, null);
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The stream is already disposed; a stuck read cannot outlive it for long.
        }

        // The write gate is deliberately not disposed: it holds no OS resources, and a request that is still waiting for
        // it when the connection is disposed must fail with a WorkerException, not an ObjectDisposedException.
        _lifetime.Dispose();
    }

    private async Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _closing) == 1)
            {
                throw new WorkerException(WorkerErrorKind.ConnectionLost, "The worker connection was closed.");
            }

            await WorkerFraming.WriteFrameAsync(_stream, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A cancelled write that the caller did not ask for means the pipe was closed under it.
            Close(WorkerConnectionCloseReason.IoError, ex.GetType().Name);
            throw new WorkerException(WorkerErrorKind.ConnectionLost, "The worker connection was lost while sending.", null, ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var frame = await WorkerFraming.ReadFrameAsync(_stream, _lifetime.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    Close(WorkerConnectionCloseReason.PeerClosed, "The worker closed the connection.");
                    return;
                }

                var envelope = WorkerMessageSerializer.Deserialize(frame.Payload);
                Route(envelope);
            }
        }
        catch (OperationCanceledException)
        {
            Close(WorkerConnectionCloseReason.Disposed, null);
        }
        catch (WorkerProtocolException ex)
        {
            Close(WorkerConnectionCloseReason.ProtocolViolation, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Close(WorkerConnectionCloseReason.IoError, ex.GetType().Name);
        }
    }

    private void Route(WorkerMessageEnvelope envelope)
    {
        if (!_pending.TryGetValue(envelope.RequestId, out var request))
        {
            _log.Info($"Ignored an unsolicited or late message of type '{envelope.MessageType}'.");
            return;
        }

        if (envelope.MessageType == WorkerMessageTypes.Error)
        {
            request.Completion.TrySetException(ToException(envelope));
        }
        else if (envelope.MessageType == request.MessageType + WorkerMessageTypes.ResultSuffix)
        {
            request.Completion.TrySetResult(envelope);
        }
        else
        {
            request.Completion.TrySetException(new WorkerException(
                WorkerErrorKind.ProtocolViolation,
                $"Unexpected response type '{envelope.MessageType}' for '{request.MessageType}'."));
        }
    }

    private static WorkerException ToException(WorkerMessageEnvelope envelope)
    {
        string? code = null;
        string? message = null;
        if (envelope.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            code = payload.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            message = payload.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }

        code = Sanitize(code, 64) ?? "unknown";
        message = Sanitize(message, 200) ?? "The worker reported an error.";
        return new WorkerException(WorkerErrorKind.Rejected, $"The worker rejected the request ({code}): {message}", code);
    }

    private static string? Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var span = value.AsSpan(0, Math.Min(value.Length, maxLength));
        return string.Create(span.Length, span.ToString(), static (destination, source) =>
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    private void Close(WorkerConnectionCloseReason reason, string? detail)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            return;
        }

        if (!_lifetime.IsCancellationRequested)
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closing a broken pipe can throw; the connection is ending anyway.
        }

        var failure = reason == WorkerConnectionCloseReason.ProtocolViolation
            ? new WorkerException(WorkerErrorKind.ProtocolViolation, "The worker sent an invalid message: " + detail)
            : new WorkerException(WorkerErrorKind.ConnectionLost, "The worker connection was closed.");
        foreach (var request in _pending.Values)
        {
            request.Completion.TrySetException(failure);
        }

        _closed.TrySetResult(new WorkerConnectionClosed(reason, detail));
    }

    private sealed class PendingRequest(string messageType, long startTimestamp)
    {
        public string MessageType { get; } = messageType;

        public long StartTimestamp { get; } = startTimestamp;

        public TaskCompletionSource<WorkerMessageEnvelope> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
