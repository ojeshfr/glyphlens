using System.Text.Json;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

/// <summary>Recognised regions will travel over the worker protocol (the PaddleOCR engine), so they must round-trip.</summary>
public class OcrRegionSerializationTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ARegionWithoutConfidence_RoundTrips_AndStaysWithoutConfidence()
    {
        var region = new ScreenTextRegion(new Guid("3f2b8c1e-6a4d-4f0e-9b7a-2c5d8e1f0a93"), "Hallo Welt", 4, 8, 120, 24, DetectionSource.Ocr, null, "de-DE", Stamp);

        var json = JsonSerializer.Serialize(region, WorkerMessageSerializer.Options);
        var restored = JsonSerializer.Deserialize<ScreenTextRegion>(json, WorkerMessageSerializer.Options);

        Assert.Contains("\"confidence\":null", json, StringComparison.Ordinal);
        Assert.Equal(region, restored);
        Assert.Null(restored!.Confidence);
    }

    [Fact]
    public void ARegionWithConfidence_StillRoundTrips()
    {
        var region = new ScreenTextRegion(Guid.NewGuid(), "x", 0, 0, 5, 5, DetectionSource.Ocr, 0.87, "en-US", Stamp);

        var restored = JsonSerializer.Deserialize<ScreenTextRegion>(JsonSerializer.Serialize(region, WorkerMessageSerializer.Options), WorkerMessageSerializer.Options);

        Assert.Equal(0.87, restored!.Confidence);
    }
}
