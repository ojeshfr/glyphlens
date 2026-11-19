using System.Buffers;
using System.Buffers.Binary;

namespace GlyphLens.Infrastructure.IPC;

/// <summary>A frame violated the framing rules (zero length, too large, or truncated). The stream cannot be resynchronised.</summary>
public sealed class WorkerFramingException : WorkerProtocolException
{
    public WorkerFramingException()
    {
    }

    public WorkerFramingException(string message)
        : base(message)
    {
    }

    public WorkerFramingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One received frame's payload, in a pooled buffer that is returned by <see cref="Dispose"/>.
/// </summary>
public sealed class WorkerFrame : IDisposable
{
    private byte[]? _buffer;

    internal WorkerFrame(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }

    /// <summary>The payload bytes. Valid until the frame is disposed.</summary>
    public ReadOnlySpan<byte> Payload =>
        new(_buffer ?? throw new ObjectDisposedException(nameof(WorkerFrame)), 0, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Length-prefixed message framing: a 4-byte unsigned little-endian length, then that many bytes of UTF-8 JSON.
/// </summary>
/// <remarks>
/// <para>
/// Framing is independent of serialization: this class moves opaque byte payloads. It never assumes that one
/// read corresponds to one frame; it loops until the header or payload is complete.
/// </para>
/// <para>
/// The announced length is validated <b>before</b> any payload buffer is allocated, so a hostile or corrupt length
/// cannot make the receiver allocate more than <see cref="MaxFrameBytes"/>. Zero-length frames are rejected.
/// </para>
/// </remarks>
public static class WorkerFraming
{
    public const int HeaderSize = 4;

    /// <summary>Largest accepted payload: 16 MiB.</summary>
    public const int MaxFrameBytes = WorkerMessageSerializer.MaxMessageBytes;

    /// <summary>
    /// Reads one frame.
    /// </summary>
    /// <returns>The frame, or <see langword="null"/> when the stream ended cleanly between frames.</returns>
    /// <exception cref="WorkerFramingException">Zero or oversized length, or the stream ended inside a frame.</exception>
    public static async ValueTask<WorkerFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken, int maxFrameBytes = MaxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        uint length;
        try
        {
            var read = await ReadUntilFullAsync(stream, header, HeaderSize, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            if (read < HeaderSize)
            {
                throw new WorkerFramingException("The connection ended inside a frame header.");
            }

            length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        if (length == 0)
        {
            throw new WorkerFramingException("Zero-length frames are not allowed.");
        }

        if (length > (uint)maxFrameBytes)
        {
            throw new WorkerFramingException($"Frame length {length} exceeds the {maxFrameBytes}-byte limit.");
        }

        var payloadLength = (int)length;
        var buffer = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            var read = await ReadUntilFullAsync(stream, buffer, payloadLength, cancellationToken).ConfigureAwait(false);
            if (read < payloadLength)
            {
                throw new WorkerFramingException("The connection ended inside a frame payload.");
            }

            return new WorkerFrame(buffer, payloadLength);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Writes one frame as a single contiguous write (so concurrent writers, if serialised by the caller, never interleave bytes).</summary>
    /// <exception cref="WorkerFramingException">The payload is empty or exceeds the limit.</exception>
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, int maxFrameBytes = MaxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length == 0)
        {
            throw new WorkerFramingException("Zero-length frames are not allowed.");
        }

        if (payload.Length > maxFrameBytes)
        {
            throw new WorkerFramingException($"Payload length {payload.Length} exceeds the {maxFrameBytes}-byte limit.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + payload.Length);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payload.Length);
            payload.CopyTo(buffer.AsMemory(HeaderSize));
            await stream.WriteAsync(buffer.AsMemory(0, HeaderSize + payload.Length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads until <paramref name="count"/> bytes are in <paramref name="buffer"/> or the stream ends; returns the number read.</summary>
    private static async ValueTask<int> ReadUntilFullAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
