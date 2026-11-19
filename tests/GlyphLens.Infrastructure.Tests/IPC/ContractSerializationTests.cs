using System.Text.Json;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.IPC;

/// <summary>
/// Core models travel inside IPC payloads; they must round-trip through the shared protocol options
/// and keep their validation when deserialized.
/// </summary>
public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions Options = WorkerMessageSerializer.Options;

    [Fact]
    public void ScreenTextRegion_RoundTripsWithCamelCaseAndStringEnum()
    {
        var region = new ScreenTextRegion(
            new Guid("3f2b8c1e-6a4d-4f0e-9b7a-2c5d8e1f0a93"),
            "Guten Tag",
            -1280,
            40,
            200,
            30,
            DetectionSource.Accessibility,
            1.0,
            "de",
            new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(region, Options);
        var restored = JsonSerializer.Deserialize<ScreenTextRegion>(json, Options);

        Assert.Contains("\"detectionSource\":\"accessibility\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceLanguage\":\"de\"", json, StringComparison.Ordinal);
        Assert.Equal(region, restored);
    }

    [Fact]
    public void ScreenTextRegion_DeserializationEnforcesValidation()
    {
        const string Json = """
            {"id":"3f2b8c1e-6a4d-4f0e-9b7a-2c5d8e1f0a93","text":"x","x":0,"y":0,"width":10,"height":10,
             "detectionSource":"ocr","confidence":1.5,"sourceLanguage":null,"timestamp":"2026-01-15T10:30:00Z"}
            """;

        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Deserialize<ScreenTextRegion>(Json, Options));
    }

    [Fact]
    public void DetectionSource_RejectsIntegerValues()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DetectionSource>("2", Options));
    }

    [Fact]
    public void TranslationRequest_RoundTripsWithNullSourceLanguage()
    {
        var request = new TranslationRequest("Bonjour", null, "en");

        var json = JsonSerializer.Serialize(request, Options);
        var restored = JsonSerializer.Deserialize<TranslationRequest>(json, Options);

        Assert.Equal("""{"text":"Bonjour","sourceLanguage":null,"targetLanguage":"en"}""", json);
        Assert.Equal(request, restored);
    }

    [Fact]
    public void TranslationResult_RoundTrips()
    {
        var result = new TranslationResult("Bonjour", "Hello", "fr", "en", "argos-fr-en@1.9");

        var restored = JsonSerializer.Deserialize<TranslationResult>(JsonSerializer.Serialize(result, Options), Options);

        Assert.Equal(result, restored);
    }

    [Fact]
    public void TranslationResult_RejectsMissingRequiredField()
    {
        const string Json = """{"originalText":"Bonjour","translatedText":"Hello","sourceLanguage":"fr","targetLanguage":"en"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranslationResult>(Json, Options));
    }

    [Fact]
    public void WorkerStatus_SerializesAsCamelCaseString()
    {
        Assert.Equal("\"unavailable\"", JsonSerializer.Serialize(WorkerStatus.Unavailable, Options));
    }
}
