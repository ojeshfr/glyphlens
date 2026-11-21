using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.Ocr;
using GlyphLens.Infrastructure.Tests.Translation;

namespace GlyphLens.Infrastructure.Tests.Ocr;

public sealed class WorkerOcrEngineTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static CapturedFrame Frame(int width = 400, int height = 300)
    {
        var metadata = new FrameMetadata("window:1", width, height, width * 4, PixelFormat.Bgra8, Stamp, 1);
        return new CapturedFrame(metadata, new byte[metadata.ByteLength]);
    }

    private static (WorkerOcrEngine Engine, FakeWorkerRequestChannel Channel, FakeEncoder Encoder) Create(TimeSpan? timeout = null)
    {
        var channel = new FakeWorkerRequestChannel();
        var encoder = new FakeEncoder();
        return (new WorkerOcrEngine(channel, encoder, timeout), channel, encoder);
    }

    private static object Region(string text, int x, int y, int w, int h, double? confidence = 0.97) =>
        new { text, x, y, width = w, height = h, confidence };

    private static object Answer(params object[] regions) => new { engine = "fake", width = 400, height = 300, elapsedMs = 5, regions };

    private sealed class FakeEncoder : IFrameImageEncoder
    {
        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        public Task<byte[]> EncodePngAsync(CapturedFrame frame, CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is { } failure ? throw failure : Task.FromResult<byte[]>([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        }
    }

    [Fact]
    public async Task RecognizeAsync_SendsTheFrameAsABase64Png_AndMapsTheRegions()
    {
        var (engine, channel, _) = Create();
        channel.Respond(Answer(Region("你好，世界", 10, 20, 120, 30, 0.991), Region("Hello", 10, 60, 50, 20, null)));

        var regions = await engine.RecognizeAsync(Frame(), CancellationToken.None);

        var call = Assert.Single(channel.Calls);
        Assert.Equal("ocr.detect", call.MessageType);
        Assert.Equal(WorkerOcrEngine.DefaultTimeout, call.Timeout);
        var image = call.Json.GetProperty("image");
        Assert.Equal("png", image.GetProperty("format").GetString());
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], Convert.FromBase64String(image.GetProperty("dataBase64").GetString()!));

        Assert.Equal(2, regions.Count);
        Assert.Equal("你好，世界", regions[0].Text);
        Assert.Equal((10, 20, 120, 30), (regions[0].X, regions[0].Y, regions[0].Width, regions[0].Height));
        Assert.Equal(0.991, regions[0].Confidence);
        Assert.Equal(DetectionSource.Ocr, regions[0].DetectionSource);
        Assert.Null(regions[1].Confidence); // reported as absent: nothing is invented
        Assert.NotEqual(regions[0].Id, regions[1].Id);
    }

    [Fact]
    public async Task RecognizeAsync_NoText_IsAnEmptyList()
    {
        var (engine, channel, _) = Create();
        channel.Respond(Answer());

        Assert.Empty(await engine.RecognizeAsync(Frame(), CancellationToken.None));
    }

    [Theory]
    [InlineData("engine_unavailable", OcrErrorKind.EngineUnavailable)]
    [InlineData("invalid_request", OcrErrorKind.FrameTooLarge)]
    [InlineData("ocr_failed", OcrErrorKind.Failed)]
    [InlineData("internal_error", OcrErrorKind.Failed)]
    public async Task RecognizeAsync_MapsWorkerErrorCodes_AndNeverReportsThemAsNoText(string code, OcrErrorKind expected)
    {
        var (engine, channel, _) = Create();
        channel.Throw(new WorkerException(WorkerErrorKind.Rejected, "The OCR runtime is not installed. Install it with: pip install paddleocr==3.7.0 onnxruntime", code));

        var ex = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(Frame(), CancellationToken.None));

        Assert.Equal(expected, ex.Kind);
        Assert.IsType<WorkerException>(ex.InnerException);
    }

    [Fact]
    public async Task RecognizeAsync_WhenTheEngineIsUnavailable_ShowsTheWorkersInstruction()
    {
        var (engine, channel, _) = Create();
        channel.Throw(new WorkerException(WorkerErrorKind.Rejected, "The OCR runtime is not installed. Install it with: pip install paddleocr==3.7.0 onnxruntime", "engine_unavailable"));

        var ex = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(Frame(), CancellationToken.None));

        Assert.Contains("pip install paddleocr", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkerErrorKind.NotConnected)]
    [InlineData(WorkerErrorKind.ConnectionLost)]
    [InlineData(WorkerErrorKind.ProcessExited)]
    public async Task RecognizeAsync_WhenTheWorkerIsNotRunning_SaysSoAndHowToStartIt(WorkerErrorKind kind)
    {
        var (engine, channel, _) = Create();
        channel.Throw(new WorkerException(kind, "gone"));

        var ex = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(Frame(), CancellationToken.None));

        Assert.Equal(OcrErrorKind.EngineUnavailable, ex.Kind);
        Assert.Contains("worker", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecognizeAsync_ATimeout_IsAFailureNotAnEmptyResult()
    {
        var (engine, channel, _) = Create();
        channel.Throw(new WorkerException(WorkerErrorKind.RequestTimeout, "timeout"));

        var ex = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(Frame(), CancellationToken.None));

        Assert.Equal(OcrErrorKind.Failed, ex.Kind);
    }

    public static TheoryData<object?> MalformedAnswers => new()
    {
        null,
        new { regions = "x" },
        new { engine = "fake" },
        Answer(new { text = "a", x = 0, y = 0, width = 10 }),                              // missing height
        Answer(new { text = 5, x = 0, y = 0, width = 10, height = 10 }),                  // text not a string
        Answer(Region("", 0, 0, 10, 10)),                                                // blank text
        Answer(Region("a", -1, 0, 10, 10)),                                              // outside the frame
        Answer(Region("a", 395, 0, 10, 10)),                                             // overflows the frame
        Answer(Region("a", 0, 295, 10, 10)),
        Answer(Region("a", 0, 0, 0, 10)),                                                // empty box
        Answer(Region("a", 0, 0, 10, 10, 1.5)),                                          // confidence out of range
        Answer(Region("a", 0, 0, 10, 10, -0.1)),
    };

    [Theory]
    [MemberData(nameof(MalformedAnswers))]
    public async Task RecognizeAsync_RejectsMalformedAnswers_InsteadOfGuessing(object? answer)
    {
        var (engine, channel, _) = Create();
        channel.Respond(answer);

        var ex = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(Frame(), CancellationToken.None));

        Assert.Equal(OcrErrorKind.Failed, ex.Kind);
    }

    [Fact]
    public async Task RecognizeAsync_AnEncoderFailure_IsPassedOn_WithoutContactingTheWorker()
    {
        var (engine, channel, encoder) = Create();
        encoder.Failure = new OcrException(OcrErrorKind.Failed, "The captured frame could not be prepared for recognition.");

        await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(Frame(), CancellationToken.None));

        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task RecognizeAsync_Cancellation_Propagates()
    {
        var (engine, channel, _) = Create();
        channel.Handler = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };
        using var cts = new CancellationTokenSource();

        var pending = engine.RecognizeAsync(Frame(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task RecognizeAsync_NeverHasTwoRequestsInFlight()
    {
        var (engine, channel, _) = Create();
        channel.Handler = async (_, ct) =>
        {
            await Task.Delay(30, ct);
            return Answer();
        };

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => engine.RecognizeAsync(Frame(), CancellationToken.None)));

        Assert.Equal(1, channel.MaxInFlight);
        Assert.Equal(4, channel.Calls.Count);
    }

    [Fact]
    public async Task ErrorMessages_NeverContainRecognisedText()
    {
        var (engine, channel, _) = Create();
        channel.Respond(Answer(Region("TOP-SECRET-SCREEN-TEXT", 500, 0, 10, 10)));

        var ex = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(Frame(), CancellationToken.None));

        Assert.DoesNotContain("TOP-SECRET", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ValidatesItsArguments()
    {
        var channel = new FakeWorkerRequestChannel();
        var encoder = new FakeEncoder();

        Assert.Throws<ArgumentNullException>(() => new WorkerOcrEngine(null!, encoder));
        Assert.Throws<ArgumentNullException>(() => new WorkerOcrEngine(channel, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerOcrEngine(channel, encoder, TimeSpan.Zero));
    }
}
