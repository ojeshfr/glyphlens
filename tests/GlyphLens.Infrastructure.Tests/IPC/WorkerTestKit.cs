using System.IO.Pipes;
using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

/// <summary>A connected pair of real (loopback) named-pipe streams: one for the code under test, one driven by the test.</summary>
internal sealed class PipePair : IAsyncDisposable
{
    private PipePair(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        Server = server;
        Client = client;
    }

    /// <summary>The "application" end, handed to the code under test.</summary>
    public NamedPipeServerStream Server { get; }

    /// <summary>The "worker" end, driven by the test.</summary>
    public NamedPipeClientStream Client { get; }

    public static async Task<PipePair> CreateAsync()
    {
        var name = "glyphlens-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
        return new PipePair(server, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Client.DisposeAsync();
    }
}

/// <summary>Test helpers for speaking the worker protocol by hand.</summary>
internal static class Wire
{
    public static async Task<WorkerMessageEnvelope> ReadAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var frame = await WorkerFraming.ReadFrameAsync(stream, cts.Token) ?? throw new EndOfStreamException();
        return WorkerMessageSerializer.Deserialize(frame.Payload);
    }

    public static Task WriteAsync(Stream stream, string requestId, string messageType, object? payload = null)
    {
        var element = payload is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(payload, WorkerMessageSerializer.Options);
        var envelope = new WorkerMessageEnvelope("1.0", requestId, messageType, element, DateTimeOffset.UtcNow);
        return WorkerFraming.WriteFrameAsync(stream, WorkerMessageSerializer.Serialize(envelope), CancellationToken.None).AsTask();
    }

    public static async Task WriteRawFrameAsync(Stream stream, byte[] payload)
    {
        await WorkerFraming.WriteFrameAsync(stream, payload, CancellationToken.None);
    }

    public static async Task WriteRawBytesAsync(Stream stream, byte[] bytes)
    {
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public static byte[] Header(uint length)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, length);
        return bytes;
    }
}

internal sealed class RecordingLog : IWorkerLog
{
    private readonly List<WorkerRequestLogEntry> _requests = [];
    private readonly List<string> _events = [];

    public IReadOnlyList<WorkerRequestLogEntry> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Request(WorkerRequestLogEntry entry)
    {
        lock (_requests)
        {
            _requests.Add(entry);
        }
    }

    public void Info(string message)
    {
        lock (_events)
        {
            _events.Add(message);
        }
    }
}

/// <summary>A stream that returns at most <c>chunk</c> bytes per read, to prove partial reads are handled.</summary>
internal sealed class ChunkedReadStream(byte[] data, int chunk) : Stream
{
    private int _position;

    public int BytesServed => _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
