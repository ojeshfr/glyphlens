using System.Globalization;
using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Ocr;

/// <summary>
/// Recognises text with the Python worker's local OCR engine (PaddleOCR PP-OCRv5 through ONNX Runtime) over the
/// authenticated pipe: the frame is encoded as PNG in memory, sent with <c>ocr.detect</c>, and the worker answers with the
/// recognised lines, their boxes and the recogniser's own confidence.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is invented: a missing runtime or missing models are reported as an <see cref="OcrException"/> with the
/// worker's instruction, never as an empty "no text" result. Frames are never written to disk or logged. Calls are
/// serialised (one request in flight): the worker handles one request at a time anyway, and a Live loop must never queue
/// frames behind a slow recognition.
/// </para>
/// <para>See docs/IPC-PROTOCOL.md, message <c>ocr.detect</c>.</para>
/// </remarks>
public sealed class WorkerOcrEngine : IOcrEngine, IDisposable
{
    /// <summary>How long an <c>ocr.detect</c> request may take (the first one also loads the models).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private const int MaxRegions = 2000;
    private const int MaxTextLength = 4096;

    private readonly IWorkerRequestChannel _channel;
    private readonly IFrameImageEncoder _encoder;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _turn = new(1, 1);

    public WorkerOcrEngine(IWorkerRequestChannel channel, IFrameImageEncoder encoder, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(encoder);
        if (timeout is { } t && t <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        _channel = channel;
        _encoder = encoder;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var png = await _encoder.EncodePngAsync(frame, cancellationToken).ConfigureAwait(false);
            var payload = new { image = new { format = "png", dataBase64 = Convert.ToBase64String(png) } };

            JsonElement? response;
            try
            {
                response = await _channel.RequestAsync(WorkerMessageTypes.OcrDetect, payload, _timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkerException ex)
            {
                throw Map(ex);
            }

            return Parse(response, frame.Metadata);
        }
        finally
        {
            _ = _turn.Release();
        }
    }

    public void Dispose() => _turn.Dispose();

    private static OcrException Map(WorkerException ex) => ex.Kind switch
    {
        WorkerErrorKind.Rejected when ex.ErrorCode == "engine_unavailable" =>
            new OcrException(OcrErrorKind.EngineUnavailable, WithHint("The local OCR engine is not available", ex.Message), ex),
        WorkerErrorKind.Rejected when ex.ErrorCode == "invalid_request" =>
            new OcrException(OcrErrorKind.FrameTooLarge, "The frame is too large for the local OCR engine.", ex),
        WorkerErrorKind.Rejected => new OcrException(OcrErrorKind.Failed, "The local OCR engine could not recognise this frame.", ex),
        WorkerErrorKind.RequestTimeout => new OcrException(OcrErrorKind.Failed, "The local OCR engine did not answer in time.", ex),
        WorkerErrorKind.ProtocolViolation => new OcrException(OcrErrorKind.Failed, "The local OCR engine sent an invalid answer.", ex),
        _ => new OcrException(OcrErrorKind.EngineUnavailable, "The local worker is not running. Start it from the Worker panel (or turn translation on).", ex),
    };

    /// <summary>The worker's own message already says how to fix the problem (for example the pip command); show it, bounded.</summary>
    private static string WithHint(string prefix, string workerMessage)
    {
        var message = workerMessage.Length > 240 ? workerMessage[..240] : workerMessage;
        return string.IsNullOrWhiteSpace(message) ? prefix + "." : $"{prefix}: {message}";
    }

    private static List<ScreenTextRegion> Parse(JsonElement? payload, FrameMetadata frame)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body ||
            !body.TryGetProperty("regions", out var regionsElement) ||
            regionsElement.ValueKind != JsonValueKind.Array ||
            regionsElement.GetArrayLength() > MaxRegions)
        {
            throw Violation();
        }

        var now = DateTimeOffset.UtcNow;
        var regions = new List<ScreenTextRegion>(regionsElement.GetArrayLength());
        foreach (var item in regionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Violation();
            }

            var text = ReadString(item, "text");
            var x = ReadInt(item, "x");
            var y = ReadInt(item, "y");
            var width = ReadInt(item, "width");
            var height = ReadInt(item, "height");
            double? confidence = item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;

            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength || width < 1 || height < 1 ||
                x < 0 || y < 0 || x + width > frame.Width || y + height > frame.Height ||
                confidence is { } value && (double.IsNaN(value) || value < 0.0 || value > 1.0))
            {
                throw Violation();
            }

            regions.Add(new ScreenTextRegion(Guid.NewGuid(), text.Trim(), x, y, width, height, DetectionSource.Ocr, confidence, null, now));
        }

        return regions;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : throw Violation();

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Violation();

    private static OcrException Violation() =>
        new(OcrErrorKind.Failed, string.Create(CultureInfo.InvariantCulture, $"The local OCR engine sent an invalid answer."));
}
