using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

/// <summary>A scriptable OCR engine and language source. Records what it was asked to read.</summary>
internal sealed class FakeOcr : IOcrEngine, IOcrLanguageSource
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    public List<ScreenTextRegion> Result { get; set; } =
    [
        Region("Hello world", 10, 20, 200, 30),
        Region("Second line", 10, 70, 180, 28),
    ];

    public int Calls { get; private set; }

    public int LastFrameWidth { get; private set; }

    public Exception? Failure { get; set; }

    /// <summary>When set, recognition waits for this task (or cancellation) before returning.</summary>
    public Task? Gate { get; set; }

    public IReadOnlyList<OcrLanguage> AvailableLanguages { get; set; } = [new("en-US", "English (United States)"), new("de-DE", "German")];

    public string? SelectedLanguage { get; set; }

    public static ScreenTextRegion Region(string text, int x, int y, int width, int height) =>
        new(Guid.NewGuid(), text, x, y, width, height, DetectionSource.Ocr, null, "en-US", Stamp);

    public async Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        Calls++;
        LastFrameWidth = frame.Metadata.Width;
        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Failure is null ? Result : throw Failure;
    }
}
