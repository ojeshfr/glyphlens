using System.Runtime.InteropServices;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GlyphLens.Windows.Ocr;

/// <summary>
/// <see cref="Core.Abstractions.IOcrEngine"/> implemented with the Windows built-in OCR (<c>Windows.Media.Ocr</c>).
/// </summary>
/// <remarks>
/// <para>
/// It runs fully offline and locally, needs no downloads and no extra packages, and uses whatever OCR language packs
/// Windows has installed (<see cref="AvailableLanguages"/>). Languages are added in Windows Settings; this class never
/// installs anything.
/// </para>
/// <para>
/// One <see cref="ScreenTextRegion"/> is returned per recognised line, with pixel bounds relative to the frame (the union of
/// the line's word boxes). Windows OCR does not report confidence, so <see cref="ScreenTextRegion.Confidence"/> is
/// <see langword="null"/>. Text orientation is not reported either: rotated text is returned as the engine reads it.
/// </para>
/// <para>Recognised text is held only in the returned regions; it is never logged or stored.</para>
/// </remarks>
public sealed class WindowsOcrEngine : IOcrEngine, IOcrLanguageSource, IDisposable
{
    private const int BytesPerPixel = FrameMetadata.BytesPerPixel;

    private readonly SemaphoreSlim _recognizeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string? SelectedLanguage { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<OcrLanguage> AvailableLanguages
    {
        get
        {
            try
            {
                return [.. OcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguage(l.LanguageTag, l.DisplayName))];
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                return [];
            }
        }
    }

    public void Dispose() => _recognizeGate.Dispose();

    /// <summary>The largest width or height, in pixels, the engine accepts.</summary>
    public static int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = frame.Metadata;
        if (metadata.Width > MaxImageDimension || metadata.Height > MaxImageDimension)
        {
            throw new OcrException(
                OcrErrorKind.FrameTooLarge,
                $"The frame ({metadata.Width}×{metadata.Height}) is larger than Windows OCR supports ({MaxImageDimension} px per side).");
        }

        var engine = GetEngine(SelectedLanguage);

        // A Windows OcrEngine rejects overlapping RecognizeAsync calls ("Another RecognizeAsync operation is already
        // running"), so concurrent callers take turns. Each caller waits only for its own turn and can cancel while waiting.
        await _recognizeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var bitmap = CreateBitmap(frame);
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            return Map(result, metadata, engine.RecognizerLanguage.LanguageTag);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // The message of a Windows exception is generic; the text being recognised is never part of it.
            throw new OcrException(OcrErrorKind.Failed, "Windows OCR failed to process the frame.", ex);
        }
        finally
        {
            _recognizeGate.Release();
        }
    }

    /// <summary>
    /// The union of a line's word rectangles, clamped to the frame, as integer pixels. Null when nothing visible remains.
    /// </summary>
    internal static ScreenRectangle? UnionBounds(IEnumerable<(double X, double Y, double Width, double Height)> words, int frameWidth, int frameHeight)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        var any = false;
        foreach (var (x, y, width, height) in words)
        {
            any = true;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + width);
            bottom = Math.Max(bottom, y + height);
        }

        if (!any)
        {
            return null;
        }

        var x0 = Math.Clamp((int)Math.Floor(left), 0, frameWidth - 1);
        var y0 = Math.Clamp((int)Math.Floor(top), 0, frameHeight - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(right), x0 + 1, frameWidth);
        var y1 = Math.Clamp((int)Math.Ceiling(bottom), y0 + 1, frameHeight);
        return new ScreenRectangle(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Copies a frame's pixels into a tightly packed array (no row padding) of exactly <c>width * height * 4</c> bytes.</summary>
    internal static byte[] ToTightPixels(CapturedFrame frame)
    {
        var metadata = frame.Metadata;
        var rowBytes = metadata.Width * BytesPerPixel;
        var tight = new byte[rowBytes * metadata.Height];
        var source = frame.Pixels.Span;

        if (metadata.Stride == rowBytes)
        {
            source[..tight.Length].CopyTo(tight);
            return tight;
        }

        for (var row = 0; row < metadata.Height; row++)
        {
            source.Slice(row * metadata.Stride, rowBytes).CopyTo(tight.AsSpan(row * rowBytes, rowBytes));
        }

        return tight;
    }

    internal static SoftwareBitmap CreateBitmap(CapturedFrame frame)
    {
        var metadata = frame.Metadata;
        var pixels = ToTightPixels(frame);

        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        var buffer = writer.DetachBuffer();
        return SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            BitmapPixelFormat.Bgra8,
            metadata.Width,
            metadata.Height,
            BitmapAlphaMode.Ignore);
    }

    private static List<ScreenTextRegion> Map(OcrResult result, FrameMetadata metadata, string languageTag)
    {
        var regions = new List<ScreenTextRegion>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var bounds = UnionBounds(
                line.Words.Select(w => (w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height)),
                metadata.Width,
                metadata.Height);
            if (bounds is not { } box)
            {
                continue;
            }

            regions.Add(new ScreenTextRegion(
                Guid.NewGuid(),
                text,
                box.X,
                box.Y,
                box.Width,
                box.Height,
                DetectionSource.Ocr,
                confidence: null,
                LanguageCode.IsValid(languageTag) ? languageTag : null,
                metadata.Timestamp));
        }

        return regions;
    }

    private OcrEngine GetEngine(string? languageTag)
    {
        var key = languageTag ?? string.Empty;
        lock (_gate)
        {
            if (_engines.TryGetValue(key, out var cached))
            {
                return cached;
            }

            OcrEngine? engine;
            try
            {
                engine = languageTag is null
                    ? OcrEngine.TryCreateFromUserProfileLanguages()
                    : OcrEngine.TryCreateFromLanguage(new Language(languageTag));
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                throw new OcrException(
                    languageTag is null ? OcrErrorKind.EngineUnavailable : OcrErrorKind.LanguageNotInstalled,
                    languageTag is null
                        ? "Windows OCR is not available on this system."
                        : $"'{languageTag}' is not a valid OCR language.",
                    ex);
            }

            if (engine is null)
            {
                throw languageTag is null
                    ? new OcrException(
                        OcrErrorKind.EngineUnavailable,
                        "No Windows OCR language is installed for your user languages. Add an OCR-capable language in Windows Settings (Time & language).")
                    : new OcrException(
                        OcrErrorKind.LanguageNotInstalled,
                        $"The OCR language '{languageTag}' is not installed. Add it in Windows Settings (Time & language).");
            }

            _engines[key] = engine;
            return engine;
        }
    }
}
