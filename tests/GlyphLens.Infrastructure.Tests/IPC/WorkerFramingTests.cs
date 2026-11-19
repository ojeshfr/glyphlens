using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

public class WorkerFramingTests
{
    private static byte[] Frame(byte[] payload) => [.. Wire.Header((uint)payload.Length), .. payload];

    [Fact]
    public async Task ReadFrame_ReturnsThePayload()
    {
        using var stream = new MemoryStream(Frame("{\"a\":1}"u8.ToArray()));

        using var frame = await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal("{\"a\":1}"u8.ToArray(), frame.Payload.ToArray());
    }

    [Fact]
    public async Task ReadFrame_HandlesOneByteAtATimePartialReads()
    {
        var payload = new byte[500];
        Random.Shared.NextBytes(payload);
        using var stream = new ChunkedReadStream(Frame(payload), chunk: 1);

        using var frame = await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Equal(payload, frame!.Payload.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task ReadFrame_ReassemblesConsecutiveFramesAtAnyChunkSize(int chunk)
    {
        using var stream = new ChunkedReadStream([.. Frame("one"u8.ToArray()), .. Frame("two"u8.ToArray()), .. Frame("three"u8.ToArray())], chunk);

        var seen = new List<string>();
        while (await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None) is { } frame)
        {
            using (frame)
            {
                seen.Add(System.Text.Encoding.UTF8.GetString(frame.Payload));
            }
        }

        Assert.Equal(["one", "two", "three"], seen);
    }

    [Fact]
    public async Task ReadFrame_ReturnsNullOnCleanEndOfStreamBetweenFrames()
    {
        using var stream = new MemoryStream([]);

        Assert.Null(await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsZeroLengthFrames()
    {
        using var stream = new MemoryStream(Wire.Header(0));

        var ex = await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Contains("Zero-length", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFrame_RejectsAnOversizedLengthBeforeReadingAnyPayload()
    {
        using var stream = new ChunkedReadStream([.. Wire.Header((uint)WorkerFraming.MaxFrameBytes + 1), .. new byte[64]], chunk: 4096);

        await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None));

        Assert.Equal(4, stream.BytesServed); // header only: the payload was neither read nor allocated
    }

    [Fact]
    public async Task ReadFrame_RejectsTheLargestPossibleLengthField()
    {
        using var stream = new MemoryStream(Wire.Header(uint.MaxValue));

        await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_AcceptsExactlyTheMaximumLength_AndRejectsOneMore()
    {
        using var atLimit = new MemoryStream(Frame(new byte[256]));
        using var overLimit = new MemoryStream(Frame(new byte[257]));

        using var frame = await WorkerFraming.ReadFrameAsync(atLimit, CancellationToken.None, maxFrameBytes: 256);
        Assert.Equal(256, frame!.Length);
        await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.ReadFrameAsync(overLimit, CancellationToken.None, maxFrameBytes: 256));
    }

    [Fact]
    public async Task ReadFrame_RejectsAHeaderCutShort()
    {
        using var stream = new MemoryStream([0x05, 0x00]);

        var ex = await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Contains("header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFrame_RejectsAPayloadCutShort()
    {
        using var stream = new MemoryStream([.. Wire.Header(10), .. "abc"u8.ToArray()]);

        var ex = await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Contains("payload", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFrame_LengthIsLittleEndian()
    {
        using var little = new MemoryStream([0x03, 0x00, 0x00, 0x00, (byte)'a', (byte)'b', (byte)'c']);
        using var big = new MemoryStream([0x00, 0x00, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c']);

        using var frame = await WorkerFraming.ReadFrameAsync(little, CancellationToken.None);
        Assert.Equal("abc"u8.ToArray(), frame!.Payload.ToArray());
        await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.ReadFrameAsync(big, CancellationToken.None)); // 0x03000000 = 50 MiB
    }

    [Fact]
    public async Task ReadFrame_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await using var pair = await PipePair.CreateAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await WorkerFraming.ReadFrameAsync(pair.Server, cts.Token));
    }

    [Fact]
    public async Task Frame_PayloadIsInvalidAfterDispose()
    {
        using var stream = new MemoryStream(Frame("x"u8.ToArray()));
        var frame = (await WorkerFraming.ReadFrameAsync(stream, CancellationToken.None))!;

        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Payload.ToArray());
        frame.Dispose(); // idempotent
    }

    [Fact]
    public async Task WriteFrame_WritesLengthPrefixThenPayload_AsOneContiguousWrite()
    {
        using var stream = new MemoryStream();

        await WorkerFraming.WriteFrameAsync(stream, "abc"u8.ToArray(), CancellationToken.None);

        Assert.Equal([0x03, 0x00, 0x00, 0x00, (byte)'a', (byte)'b', (byte)'c'], stream.ToArray());
    }

    [Fact]
    public async Task WriteFrame_RejectsEmptyAndOversizedPayloads()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<WorkerFramingException>(async () => await WorkerFraming.WriteFrameAsync(stream, new byte[11], CancellationToken.None, maxFrameBytes: 10));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsLargeFramesOverARealPipe()
    {
        await using var pair = await PipePair.CreateAsync();
        var payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var write = WorkerFraming.WriteFrameAsync(pair.Client, payload, CancellationToken.None).AsTask();
        using var frame = await WorkerFraming.ReadFrameAsync(pair.Server, CancellationToken.None);
        await write;

        Assert.True(payload.AsSpan().SequenceEqual(frame!.Payload));
    }

    [Fact]
    public void MaxFrameBytes_IsSixteenMebibytes()
    {
        Assert.Equal(16 * 1024 * 1024, WorkerFraming.MaxFrameBytes);
        Assert.Equal(4, WorkerFraming.HeaderSize);
    }
}
