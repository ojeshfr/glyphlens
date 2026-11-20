using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Ocr;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace GlyphLens.Windows.Tests.Ocr;

/// <summary>
/// Runs a test only when Windows OCR has at least one installed language. CI images and stripped installs may have none;
/// the skip reason is then shown in the test output.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsOcrFactAttribute : FactAttribute
{
    public WindowsOcrFactAttribute()
    {
        if (OcrTestImages.UnavailableReason is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class OcrTestImages
{
    private static readonly Lazy<string?> Reason = new(() =>
        new WindowsOcrEngine().AvailableLanguages.Count == 0
            ? "No Windows OCR language is installed on this machine."
            : null);

    public static string? UnavailableReason => Reason.Value;

    public static readonly DateTimeOffset Stamp = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    /// <summary>Draws black text on white and returns the frame and the rectangle each line occupies.</summary>
    public static (CapturedFrame Frame, List<Rectangle> Lines) Render(int width, int height, float fontPixels, params string[] lines)
    {
        using var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
        var drawn = new List<Rectangle>();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Arial", fontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
            var y = 40f;
            foreach (var line in lines)
            {
                var size = graphics.MeasureString(line, font);
                graphics.DrawString(line, font, Brushes.Black, 40f, y);
                drawn.Add(new Rectangle(40, (int)y, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
                y += size.Height + 40f;
            }
        }

        return (ToFrame(bitmap, extraStridePixels: 0), drawn);
    }

    public static CapturedFrame ToFrame(Bitmap bitmap, int extraStridePixels)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bitmap.Width * FrameMetadata.BytesPerPixel;
            var stride = rowBytes + (extraStridePixels * FrameMetadata.BytesPerPixel);
            var pixels = new byte[stride * bitmap.Height];
            for (var row = 0; row < bitmap.Height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (row * data.Stride), pixels, row * stride, rowBytes);
                // Garbage in the padding must never be read as pixels.
                Array.Fill(pixels, (byte)0xAB, (row * stride) + rowBytes, stride - rowBytes);
            }

            return new CapturedFrame(new FrameMetadata("test:ocr", bitmap.Width, bitmap.Height, stride, Core.Models.PixelFormat.Bgra8, Stamp, 1), pixels);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }


    public static CapturedFrame Blank(int width, int height, int fill = 0xFF)
    {
        var pixels = new byte[width * height * FrameMetadata.BytesPerPixel];
        Array.Fill(pixels, (byte)fill);
        return new CapturedFrame(new FrameMetadata("test:blank", width, height, width * FrameMetadata.BytesPerPixel, Core.Models.PixelFormat.Bgra8, Stamp, 1), pixels);
    }

    public static string Normalise(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());
}

public class WindowsOcrEngineTests
{
    [WindowsOcrFact]
    public async Task RecognisesRenderedText()
    {
        var (frame, _) = OcrTestImages.Render(900, 220, 44, "Hello World 2026");
        using var _1 = frame;
        var engine = new WindowsOcrEngine();

        var regions = await engine.RecognizeAsync(frame, CancellationToken.None);

        var text = OcrTestImages.Normalise(string.Concat(regions.Select(r => r.Text))).ToLowerInvariant();
        Assert.Contains("hello", text, StringComparison.Ordinal);
        Assert.Contains("world", text, StringComparison.Ordinal);
        Assert.Contains("2026", text, StringComparison.Ordinal);
    }

    [WindowsOcrFact]
    public async Task ReportsFrameRelativeBounds_ThatMatchWhereTheTextWasDrawn()
    {
        var (frame, drawn) = OcrTestImages.Render(900, 220, 44, "Hello World 2026");
        using var _1 = frame;

        var regions = await new WindowsOcrEngine().RecognizeAsync(frame, CancellationToken.None);

        var region = Assert.Single(regions);
        var expected = drawn[0];
        var overlap = Rectangle.Intersect(new Rectangle(region.X, region.Y, region.Width, region.Height), expected);
        Assert.True(region.X >= 0 && region.Y >= 0 && region.X + region.Width <= 900 && region.Y + region.Height <= 220, "bounds stay inside the frame");
        Assert.True(overlap.Width * overlap.Height > 0.5 * expected.Width * expected.Height, $"region {region.Bounds} should cover drawn text {expected}");
    }

    [WindowsOcrFact]
    public async Task ReturnsOneRegionPerLine_InReadingOrder()
    {
        var (frame, drawn) = OcrTestImages.Render(900, 420, 44, "First line of text", "Second line here");
        using var _1 = frame;

        var regions = await new WindowsOcrEngine().RecognizeAsync(frame, CancellationToken.None);

        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].Y < regions[1].Y);
        Assert.Contains("first", OcrTestImages.Normalise(regions[0].Text).ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("second", OcrTestImages.Normalise(regions[1].Text).ToLowerInvariant(), StringComparison.Ordinal);
        Assert.True(regions[0].Y < drawn[1].Y && regions[1].Y >= drawn[0].Bottom - 10);
    }

    [WindowsOcrFact]
    public async Task RegionMetadata_IsHonest()
    {
        var (frame, _) = OcrTestImages.Render(900, 220, 44, "Hello World 2026");
        using var _1 = frame;

        var regions = await new WindowsOcrEngine().RecognizeAsync(frame, CancellationToken.None);

        var region = Assert.Single(regions);
        Assert.Equal(DetectionSource.Ocr, region.DetectionSource);
        Assert.Null(region.Confidence); // Windows OCR reports none, and none is invented
        Assert.NotEqual(Guid.Empty, region.Id);
        Assert.Equal(OcrTestImages.Stamp, region.Timestamp);
        Assert.True(LanguageCode.IsValid(region.SourceLanguage));
    }

    [WindowsOcrFact]
    public async Task ABlankFrame_YieldsNoRegions()
    {
        using var frame = OcrTestImages.Blank(600, 300);

        var regions = await new WindowsOcrEngine().RecognizeAsync(frame, CancellationToken.None);

        Assert.Empty(regions);
    }

    [WindowsOcrFact]
    public async Task RowPadding_IsNeverReadAsPixels()
    {
        using var bitmap = new Bitmap(900, 220, DrawingPixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Arial", 44, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString("Hello World 2026", font, Brushes.Black, 40, 40);
        }

        using var padded = OcrTestImages.ToFrame(bitmap, extraStridePixels: 37);
        Assert.NotEqual(padded.Metadata.Width * 4, padded.Metadata.Stride);

        var regions = await new WindowsOcrEngine().RecognizeAsync(padded, CancellationToken.None);

        var text = OcrTestImages.Normalise(string.Concat(regions.Select(r => r.Text))).ToLowerInvariant();
        Assert.Contains("hello", text, StringComparison.Ordinal);
        Assert.Contains("2026", text, StringComparison.Ordinal);
    }

    [WindowsOcrFact]
    public async Task AnExplicitlySelectedInstalledLanguage_IsUsed()
    {
        var engine = new WindowsOcrEngine();
        var language = engine.AvailableLanguages[0];
        engine.SelectedLanguage = language.Tag;
        var (frame, _) = OcrTestImages.Render(900, 220, 44, "Hello World 2026");
        using var _1 = frame;

        var regions = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.All(regions, r => Assert.Equal(language.Tag, r.SourceLanguage, ignoreCase: true));
    }

    [WindowsOcrFact]
    public async Task ConcurrentRecognitions_AllSucceed()
    {
        var engine = new WindowsOcrEngine();
        var (frame, _) = OcrTestImages.Render(900, 220, 44, "Hello World 2026");
        using var _1 = frame;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => engine.RecognizeAsync(frame, CancellationToken.None)));

        Assert.All(results, r => Assert.Contains("hello", OcrTestImages.Normalise(string.Concat(r.Select(x => x.Text))).ToLowerInvariant(), StringComparison.Ordinal));
    }

    [WindowsOcrFact]
    public async Task ALanguageThatIsNotInstalled_IsReportedClearly()
    {
        var engine = new WindowsOcrEngine();
        const string Tag = "ja-JP";
        if (engine.AvailableLanguages.Any(l => string.Equals(l.Tag, Tag, StringComparison.OrdinalIgnoreCase)))
        {
            return; // this machine really has it; nothing to assert
        }

        engine.SelectedLanguage = Tag;
        using var frame = OcrTestImages.Blank(100, 100);

        var ex = await Assert.ThrowsAsync<OcrException>(() => engine.RecognizeAsync(frame, CancellationToken.None));

        Assert.Equal(OcrErrorKind.LanguageNotInstalled, ex.Kind);
        Assert.Contains("Windows Settings", ex.Message, StringComparison.Ordinal);
    }

    // ---- no installed language needed ----

    [Fact]
    public async Task AFrameBeyondTheEngineLimit_IsRejectedBeforeAnyWork()
    {
        using var frame = OcrTestImages.Blank(WindowsOcrEngine.MaxImageDimension + 1, 1);

        var ex = await Assert.ThrowsAsync<OcrException>(() => new WindowsOcrEngine().RecognizeAsync(frame, CancellationToken.None));

        Assert.Equal(OcrErrorKind.FrameTooLarge, ex.Kind);
    }

    [Fact]
    public async Task ACancelledToken_ThrowsWithoutDoingWork()
    {
        using var frame = OcrTestImages.Blank(100, 100);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsOcrEngine().RecognizeAsync(frame, cts.Token));
    }

    [Fact]
    public async Task ANullFrame_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new WindowsOcrEngine().RecognizeAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void AvailableLanguages_AreValidTagsWithDisplayNames()
    {
        var languages = new WindowsOcrEngine().AvailableLanguages;

        Assert.All(languages, l =>
        {
            Assert.True(LanguageCode.IsValid(l.Tag), l.Tag);
            Assert.False(string.IsNullOrWhiteSpace(l.DisplayName));
        });
    }

    [Fact]
    public void SelectedLanguage_DefaultsToTheProfileLanguages()
    {
        Assert.Null(new WindowsOcrEngine().SelectedLanguage);
    }

    [Fact]
    public void MaxImageDimension_IsWhatWindowsReports()
    {
        Assert.Equal(10_000, WindowsOcrEngine.MaxImageDimension);
    }
}

public class WindowsOcrMappingTests
{
    [Fact]
    public void UnionBounds_CoversEveryWord()
    {
        var bounds = WindowsOcrEngine.UnionBounds([(10, 20, 50, 12), (70, 22, 30, 14), (110, 19, 40, 13)], 1000, 500);

        Assert.Equal(new ScreenRectangle(10, 19, 140, 17), bounds);
    }

    [Fact]
    public void UnionBounds_RoundsOutwardSoNothingIsClipped()
    {
        var bounds = WindowsOcrEngine.UnionBounds([(10.4, 20.6, 50.2, 11.1)], 1000, 500);

        // floor(10.4)=10, floor(20.6)=20, ceil(60.6)=61, ceil(31.7)=32
        Assert.Equal(new ScreenRectangle(10, 20, 51, 12), bounds);
    }

    [Fact]
    public void UnionBounds_ClampsToTheFrame()
    {
        var bounds = WindowsOcrEngine.UnionBounds([(-5, -3, 30, 20), (990, 490, 50, 40)], 1000, 500);

        Assert.Equal(new ScreenRectangle(0, 0, 1000, 500), bounds);
    }

    [Fact]
    public void UnionBounds_NeverReturnsAZeroSizedBox()
    {
        var bounds = WindowsOcrEngine.UnionBounds([(100, 100, 0, 0)], 1000, 500);

        Assert.NotNull(bounds);
        Assert.True(bounds.Value.Width >= 1 && bounds.Value.Height >= 1);
    }

    [Fact]
    public void UnionBounds_AWordAtTheFarCorner_StaysInside()
    {
        var bounds = WindowsOcrEngine.UnionBounds([(2000, 2000, 10, 10)], 100, 50);

        Assert.NotNull(bounds);
        Assert.True(bounds.Value.X + bounds.Value.Width <= 100 && bounds.Value.Y + bounds.Value.Height <= 50);
        Assert.True(bounds.Value.X >= 0 && bounds.Value.Y >= 0);
    }

    [Fact]
    public void UnionBounds_WithNoWords_IsNull()
    {
        Assert.Null(WindowsOcrEngine.UnionBounds([], 100, 100));
    }

    [Fact]
    public void ToTightPixels_RemovesRowPadding()
    {
        // 2x2 pixels, stride 12 (4 bytes of padding per row).
        byte[] source =
        [
            1, 2, 3, 4, 5, 6, 7, 8, 0xEE, 0xEE, 0xEE, 0xEE,
            9, 10, 11, 12, 13, 14, 15, 16, 0xEE, 0xEE, 0xEE, 0xEE,
        ];
        using var frame = new CapturedFrame(new FrameMetadata("t", 2, 2, 12, Core.Models.PixelFormat.Bgra8, OcrTestImages.Stamp, 1), source);

        var tight = WindowsOcrEngine.ToTightPixels(frame);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], tight);
    }

    [Fact]
    public void ToTightPixels_CopiesAnAlreadyTightFrameExactly_EvenFromALargerPooledBuffer()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 99];
        using var frame = new CapturedFrame(new FrameMetadata("t", 2, 1, 8, Core.Models.PixelFormat.Bgra8, OcrTestImages.Stamp, 1), source);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], WindowsOcrEngine.ToTightPixels(frame));
    }
}
