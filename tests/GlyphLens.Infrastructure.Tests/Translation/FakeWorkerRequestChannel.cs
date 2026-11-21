using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;

namespace GlyphLens.Infrastructure.Tests.Translation;

/// <summary>One request that reached the fake channel, with its payload serialised exactly as the real channel would put it on the wire.</summary>
internal sealed record RecordedCall(string MessageType, JsonElement? Payload, TimeSpan Timeout)
{
    public JsonElement Json => Payload!.Value;

    /// <summary>The <c>text</c> values of a <c>translation.translate</c> request, or the <c>texts</c> of a <c>language.detect</c> request.</summary>
    public string[] SentTexts() =>
        Json.TryGetProperty("texts", out var texts)
            ? [.. texts.EnumerateArray().Select(t => t.GetString()!)]
            : [.. Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()!)];

    public JsonElement[] SentItems() => [.. Json.GetProperty("items").EnumerateArray()];

    public string[] SentIds() => [.. SentItems().Select(i => i.GetProperty("id").GetString()!)];
}

/// <summary>
/// A scriptable <see cref="IWorkerRequestChannel"/>. The handler returns the response payload: a <see cref="JsonElement"/> as
/// is, a <see cref="string"/> as raw JSON text, <see langword="null"/> for "no payload", or any other object, which is
/// serialised with the protocol options. It can also throw to simulate a failing worker.
/// </summary>
internal sealed class FakeWorkerRequestChannel : IWorkerRequestChannel
{
    private readonly object _gate = new();
    private readonly List<RecordedCall> _calls = [];
    private int _inFlight;
    private int _maxInFlight;

    public Func<RecordedCall, CancellationToken, Task<object?>> Handler { get; set; } =
        static (call, _) => throw new InvalidOperationException($"No response was scripted for '{call.MessageType}'.");

    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The most requests that were waiting for an answer at the same time.</summary>
    public int MaxInFlight
    {
        get
        {
            lock (_gate)
            {
                return _maxInFlight;
            }
        }
    }

    public IReadOnlyList<RecordedCall> CallsOf(string messageType) => [.. Calls.Where(c => c.MessageType == messageType)];

    public void Respond(Func<RecordedCall, object?> handler) => Handler = (call, _) => Task.FromResult(handler(call));

    public void Respond(object? response) => Handler = (_, _) => Task.FromResult(response);

    public void Throw(Exception exception) => Handler = (_, _) => throw exception;

    public async Task<JsonElement?> RequestAsync(string messageType, object? payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var call = new RecordedCall(
            messageType,
            payload is null ? null : JsonSerializer.SerializeToElement(payload, WorkerMessageSerializer.Options),
            timeout);
        lock (_gate)
        {
            _calls.Add(call);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        try
        {
            return ToElement(await Handler(call, cancellationToken));
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }

    private static JsonElement? ToElement(object? response)
    {
        switch (response)
        {
            case null:
                return null;
            case JsonElement element:
                return element;
            case string json:
                using (var document = JsonDocument.Parse(json))
                {
                    return document.RootElement.Clone();
                }

            default:
                return JsonSerializer.SerializeToElement(response, WorkerMessageSerializer.Options);
        }
    }
}

/// <summary>A language detector whose answers the test scripts, and which records what it was asked.</summary>
internal sealed class FakeLanguageDetector(Func<IReadOnlyList<string>, IReadOnlyList<LanguageDetectionResult>> answer) : ILanguageDetector
{
    private readonly List<string[]> _requests = [];

    public IReadOnlyList<string[]> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public Task<IReadOnlyList<LanguageDetectionResult>> DetectAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_requests)
        {
            _requests.Add([.. texts]);
        }

        return Task.FromResult(answer(texts));
    }
}

/// <summary>Builders for worker responses, shaped like docs/IPC-PROTOCOL.md.</summary>
internal static class TranslationJson
{
    public const string ModelVersion = "argos:de-en@1.9";

    public static object Ok(string id, string translated, string source = "de", string target = "en", string model = ModelVersion) =>
        new { id, status = "ok", translatedText = translated, sourceLanguage = source, targetLanguage = target, modelVersion = model, route = new[] { source, target } };

    public static object Error(string id, string code, string? requiredPackage = null) =>
        requiredPackage is null
            ? new { id, status = "error", error = new { code, message = "The worker's own message." } }
            : new { id, status = "error", error = new { code, message = "The worker's own message.", requiredPackage } };

    public static object Results(params object[] items) => new { engine = "argos-translate 1.11.0", results = items };

    /// <summary>A well-behaved worker: translates every item as <c>[source>target] text</c> with a model named after the pair.</summary>
    public static object Echo(RecordedCall call)
    {
        var target = call.Json.GetProperty("targetLanguage").GetString()!;
        var items = call.SentItems().Select(item =>
        {
            var source = item.GetProperty("sourceLanguage").GetString()!;
            return Ok(
                item.GetProperty("id").GetString()!,
                $"[{source}>{target}] {item.GetProperty("text").GetString()}",
                source,
                target,
                $"argos:{source}-{target}@1.9");
        });
        return Results([.. items]);
    }

    public static object Detection(string language, double? confidence = 0.98) =>
        new { language, confidence, alternatives = Array.Empty<object>(), issue = "none" };

    public static object NoDetection(string issue = "unknown") =>
        new { language = (string?)null, confidence = (double?)null, alternatives = Array.Empty<object>(), issue };

    public static object DetectionResults(params object[] items) => new { detector = "lingua 2.x", results = items };

    public static TranslationRequest Request(string text, string? source = "de", string target = "en") => new(text, source, target);

    /// <summary>A request whose text bypasses the constructor's checks (the constructor refuses blank text).</summary>
    public static TranslationRequest RequestWithRawText(string text, string? source = "de", string target = "en")
    {
        var request = (TranslationRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TranslationRequest));
        SetBackingField(request, nameof(TranslationRequest.Text), text);
        SetBackingField(request, nameof(TranslationRequest.SourceLanguage), source);
        SetBackingField(request, nameof(TranslationRequest.TargetLanguage), target);
        return request;
    }

    private static void SetBackingField(TranslationRequest request, string property, string? value)
    {
        var field = typeof(TranslationRequest).GetField($"<{property}>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"No backing field for {property}.");
        field.SetValue(request, value);
    }
}
