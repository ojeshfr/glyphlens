using System.Globalization;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.Translation;

namespace GlyphLens.Infrastructure.Tests.Translation;

public class WorkerTranslationEngineTests
{
    private const string Secret = "Geheimtext 4711 Passwort";
    private const string SecretTranslation = "Secret translation 4711";

    private static WorkerTranslationEngine Create(FakeWorkerRequestChannel channel, ILanguageDetector? detector = null, TranslationClientOptions? options = null) =>
        new(channel, detector, options);

    private static FakeWorkerRequestChannel EchoChannel()
    {
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(call => TranslationJson.Echo(call));
        return channel;
    }

    private static FakeWorkerRequestChannel ChannelAnswering(object? response)
    {
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(_ => response);
        return channel;
    }

    private static TranslationRequest[] Requests(int count, string target = "en") =>
        [.. Enumerable.Range(0, count).Select(i => TranslationJson.Request($"Text {i}", "de", target))];

    private static void AssertNoSecrets(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            Assert.DoesNotContain(Secret, e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretTranslation, e.Message, StringComparison.Ordinal);
        }

        if (exception is TranslationException translation)
        {
            AssertNoSecrets(translation.Error);
        }
    }

    private static void AssertNoSecrets(TranslationError error)
    {
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretTranslation, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.RequiredPackage ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- the request on the wire and the happy path ----

    [Fact]
    public async Task TranslateAsync_SendsOneItem_AndReturnsTheTranslation()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);
        const string text = "  Hallo Welt \n";

        var result = await engine.TranslateAsync(new TranslationRequest(text, "de", "en"), CancellationToken.None);

        var call = Assert.Single(channel.Calls);
        Assert.Equal("translation.translate", call.MessageType);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);
        Assert.Equal(["items", "targetLanguage"], call.Json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("en", call.Json.GetProperty("targetLanguage").GetString());
        var item = Assert.Single(call.SentItems());
        Assert.Equal(["id", "sourceLanguage", "text"], item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("0", item.GetProperty("id").GetString());
        Assert.Equal("de", item.GetProperty("sourceLanguage").GetString());
        Assert.Equal(text, item.GetProperty("text").GetString()); // sent exactly as given

        Assert.Equal(text, result.OriginalText); // the request text as given, not the worker's echo
        Assert.Equal("[de>en] " + text, result.TranslatedText);
        Assert.Equal("de", result.SourceLanguage);
        Assert.Equal("en", result.TargetLanguage);
        Assert.Equal("argos:de-en@1.9", result.ModelIdentifier);
    }

    [Fact]
    public async Task TranslateAsync_UsesTheLanguagesAndModelTheWorkerReports()
    {
        // The worker may normalise a code (pt-BR -> pt): the result says what was actually used.
        var channel = ChannelAnswering(TranslationJson.Results(TranslationJson.Ok("0", "Olá", "pt", "en", "argos:pt-en@1.0+en-fr@1.1")));
        using var engine = Create(channel);

        var result = await engine.TranslateAsync(TranslationJson.Request("Hello", "pt-BR", "en"), CancellationToken.None);

        Assert.Equal("pt", result.SourceLanguage);
        Assert.Equal("argos:pt-en@1.0+en-fr@1.1", result.ModelIdentifier);
    }

    [Fact]
    public async Task TranslateBatchAsync_ReturnsOneOutcomePerRequest_InOrder()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var outcomes = await engine.TranslateBatchAsync(
            [TranslationJson.Request("eins", "de"), TranslationJson.Request("deux", "fr"), TranslationJson.Request("tres", "es")],
            CancellationToken.None);

        Assert.Equal(["[de>en] eins", "[fr>en] deux", "[es>en] tres"], outcomes.Select(o => o.Result!.TranslatedText));
        Assert.All(outcomes, o => Assert.True(o.Succeeded));
        Assert.Equal(["0", "1", "2"], Assert.Single(channel.Calls).SentIds());
    }

    [Fact]
    public async Task TranslateBatchAsync_WithNoRequests_ReturnsNothingWithoutContactingTheWorker()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var outcomes = await engine.TranslateBatchAsync([], CancellationToken.None);

        Assert.Empty(outcomes);
        Assert.Empty(channel.Calls);
    }

    // ---- chunking ----

    [Fact]
    public async Task TranslateBatchAsync_SplitsTwentyItemsIntoRequestsOfEightEightAndFour_Sequentially()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var outcomes = await engine.TranslateBatchAsync(Requests(20), CancellationToken.None);

        Assert.Equal([8, 8, 4], channel.Calls.Select(c => c.SentItems().Length));
        Assert.Equal(1, channel.MaxInFlight); // one request in flight at a time
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i.ToString(CultureInfo.InvariantCulture)), channel.Calls[0].SentIds());
        Assert.Equal(["0", "1", "2", "3"], channel.Calls[2].SentIds()); // ids restart in every chunk
        Assert.Equal(20, outcomes.Count);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal($"[de>en] Text {i}", outcomes[i].Result!.TranslatedText); // every result at its own request
        }
    }

    [Theory]
    [InlineData(1, 7, new[] { 1, 1, 1, 1, 1, 1, 1 })]
    [InlineData(3, 7, new[] { 3, 3, 1 })]
    [InlineData(32, 40, new[] { 32, 8 })]
    [InlineData(8, 8, new[] { 8 })]
    [InlineData(8, 9, new[] { 8, 1 })]
    public async Task TranslateBatchAsync_HonoursTheConfiguredChunkSize(int chunkSize, int count, int[] expected)
    {
        var channel = EchoChannel();
        using var engine = Create(channel, options: new TranslationClientOptions { ChunkSize = chunkSize });

        await engine.TranslateBatchAsync(Requests(count), CancellationToken.None);

        Assert.Equal(expected, channel.Calls.Select(c => c.SentItems().Length));
    }

    [Fact]
    public async Task TranslateBatchAsync_GroupsItemsByTargetLanguage_AndKeepsEveryResultAtItsRequest()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);
        TranslationRequest[] requests =
        [
            TranslationJson.Request("a", "de", "en"),
            TranslationJson.Request("b", "de", "fr"),
            TranslationJson.Request("c", "de", "en"),
            TranslationJson.Request("d", "de", "fr"),
            TranslationJson.Request("e", "de", "en"),
        ];

        var outcomes = await engine.TranslateBatchAsync(requests, CancellationToken.None);

        Assert.Equal(2, channel.Calls.Count);
        Assert.Equal("en", channel.Calls[0].Json.GetProperty("targetLanguage").GetString());
        Assert.Equal(["a", "c", "e"], channel.Calls[0].SentTexts());
        Assert.Equal("fr", channel.Calls[1].Json.GetProperty("targetLanguage").GetString());
        Assert.Equal(["b", "d"], channel.Calls[1].SentTexts());
        Assert.Equal(["[de>en] a", "[de>fr] b", "[de>en] c", "[de>fr] d", "[de>en] e"], outcomes.Select(o => o.Result!.TranslatedText));
    }

    // ---- local checks that need no worker ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task TranslateBatchAsync_FailsEmptyText_WithoutContactingTheWorker(string text)
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.RequestWithRawText(text)], CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Equal(TranslationErrorKind.EmptyText, outcome.Error!.Kind);
        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task TranslateBatchAsync_FailsTextOverTheLengthLimit_ButSendsTextAtTheLimit()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);
        var atLimit = new string('a', 4096);
        var tooLong = new string('a', 4097);

        var outcomes = await engine.TranslateBatchAsync(
            [TranslationJson.Request(tooLong), TranslationJson.Request(atLimit)],
            CancellationToken.None);

        Assert.Equal(TranslationErrorKind.TextTooLong, outcomes[0].Error!.Kind);
        Assert.True(outcomes[1].Succeeded);
        Assert.Equal(4096, Assert.Single(channel.Calls).SentTexts().Single().Length); // the long one never left
    }

    [Fact]
    public async Task TranslateBatchAsync_CountsUtf16Units_ForTheLengthLimit()
    {
        var channel = EchoChannel();
        using var engine = Create(channel, options: new TranslationClientOptions { MaxTextLength = 10 });

        var outcomes = await engine.TranslateBatchAsync(
            [TranslationJson.Request(string.Concat(Enumerable.Repeat("\U0001F600", 6))), TranslationJson.Request(new string('b', 10))], // 12 and 10 units
            CancellationToken.None);

        Assert.Equal(TranslationErrorKind.TextTooLong, outcomes[0].Error!.Kind);
        Assert.True(outcomes[1].Succeeded);
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("pt-BR", "pt-br")]
    public async Task TranslateBatchAsync_FailsSameSourceAndTarget_WithoutContactingTheWorker(string source, string target)
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request("Hallo", source, target)], CancellationToken.None));

        Assert.Equal(TranslationErrorKind.SameLanguage, outcome.Error!.Kind);
        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task TranslateBatchAsync_SendsOnlyTheItemsThatNeedTheWorker_AndKeepsTheOrder()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var outcomes = await engine.TranslateBatchAsync(
            [
                TranslationJson.Request(new string('x', 5000)),
                TranslationJson.Request("eins"),
                TranslationJson.Request("gleich", "en", "en"),
                TranslationJson.Request("zwei"),
                TranslationJson.RequestWithRawText(" "),
            ],
            CancellationToken.None);

        Assert.Equal(["eins", "zwei"], Assert.Single(channel.Calls).SentTexts());
        Assert.Equal(
            [TranslationErrorKind.TextTooLong, null, TranslationErrorKind.SameLanguage, null, TranslationErrorKind.EmptyText],
            outcomes.Select(o => o.Error?.Kind));
        Assert.Equal("[de>en] eins", outcomes[1].Result!.TranslatedText);
        Assert.Equal("[de>en] zwei", outcomes[3].Result!.TranslatedText);
    }

    [Fact]
    public async Task TranslateBatchAsync_WhenEveryItemFailsLocally_NeverContactsTheWorker()
    {
        var channel = new FakeWorkerRequestChannel(); // no response scripted: any call would throw
        using var engine = Create(channel);

        var outcomes = await engine.TranslateBatchAsync(
            [TranslationJson.Request("gleich", "en", "en"), TranslationJson.Request("Hallo", null)],
            CancellationToken.None);

        Assert.All(outcomes, o => Assert.False(o.Succeeded));
        Assert.Empty(channel.Calls);
    }

    // ---- source language detection ----

    [Fact]
    public async Task TranslateBatchAsync_WithoutADetector_FailsRequestsWithoutASourceLanguage()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var outcomes = await engine.TranslateBatchAsync(
            [TranslationJson.Request("Hallo Welt", null), TranslationJson.Request("eins", "de")],
            CancellationToken.None);

        Assert.Equal(TranslationErrorKind.LanguageUndetermined, outcomes[0].Error!.Kind);
        Assert.True(outcomes[1].Succeeded);
        Assert.Equal(["eins"], Assert.Single(channel.Calls).SentTexts());
    }

    [Fact]
    public async Task TranslateBatchAsync_ResolvesMissingSourceLanguagesWithTheDetector()
    {
        var channel = EchoChannel();
        var detector = new FakeLanguageDetector(texts => [.. texts.Select(t => t.StartsWith("Bonjour", StringComparison.Ordinal)
            ? LanguageDetectionResult.Detected("fr", 0.9)
            : LanguageDetectionResult.Undetermined(LanguageDetectionIssue.Unknown))]);
        using var engine = Create(channel, detector);

        var outcomes = await engine.TranslateBatchAsync(
            [
                TranslationJson.Request("eins", "de"),
                TranslationJson.Request("Bonjour tout le monde", null),
                TranslationJson.Request("zwei", "de"),
                TranslationJson.Request("???", null),
            ],
            CancellationToken.None);

        Assert.Equal(["Bonjour tout le monde", "???"], Assert.Single(detector.Requests)); // only the texts without a source
        var call = Assert.Single(channel.Calls);
        Assert.Equal(["eins", "Bonjour tout le monde", "zwei"], call.SentTexts()); // original order
        Assert.Equal(["de", "fr", "de"], call.SentItems().Select(i => i.GetProperty("sourceLanguage").GetString()));
        Assert.Equal("[fr>en] Bonjour tout le monde", outcomes[1].Result!.TranslatedText);
        Assert.Equal("fr", outcomes[1].Result!.SourceLanguage);
        Assert.Equal(TranslationErrorKind.LanguageUndetermined, outcomes[3].Error!.Kind);
    }

    [Fact]
    public async Task TranslateBatchAsync_FailsADetectedLanguageThatEqualsTheTarget_AsSameLanguage()
    {
        var channel = EchoChannel();
        var detector = new FakeLanguageDetector(texts => [.. texts.Select(_ => LanguageDetectionResult.Detected("en", 0.99))]);
        using var engine = Create(channel, detector);

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request("Hello world", null, "en")], CancellationToken.None));

        Assert.Equal(TranslationErrorKind.SameLanguage, outcome.Error!.Kind);
        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task TranslateBatchAsync_DoesNotCallTheDetector_WhenEverySourceIsGiven()
    {
        var detector = new FakeLanguageDetector(_ => throw new InvalidOperationException("must not be called"));
        using var engine = Create(EchoChannel(), detector);

        await engine.TranslateBatchAsync(Requests(3), CancellationToken.None);

        Assert.Empty(detector.Requests);
    }

    [Fact]
    public async Task TranslateBatchAsync_MapsADetectorWorkerFailure_ToATranslationException()
    {
        var channel = EchoChannel();
        var failure = new WorkerException(WorkerErrorKind.NotConnected, "The worker is not running.");
        var detector = new FakeLanguageDetector(_ => throw failure);
        using var engine = Create(channel, detector);

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            engine.TranslateBatchAsync([TranslationJson.Request("Hallo Welt", null)], CancellationToken.None));

        Assert.Equal(TranslationErrorKind.WorkerUnavailable, ex.Error.Kind);
        Assert.Same(failure, ex.InnerException);
        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task TranslateBatchAsync_RejectsADetectorThatAnswersTheWrongCount()
    {
        var detector = new FakeLanguageDetector(_ => []);
        using var engine = Create(EchoChannel(), detector);

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            engine.TranslateBatchAsync([TranslationJson.Request("Hallo Welt", null)], CancellationToken.None));

        Assert.Equal(TranslationErrorKind.Failed, ex.Error.Kind);
    }

    [Fact]
    public async Task TranslateBatchAsync_WorksWithTheWorkerLanguageDetectorOnTheSameChannel()
    {
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(call => call.MessageType switch
        {
            "language.detect" => TranslationJson.DetectionResults(
                [.. call.SentTexts().Select(t => t.StartsWith("Bonjour", StringComparison.Ordinal) ? TranslationJson.Detection("fr") : TranslationJson.NoDetection())]),
            "translation.translate" => TranslationJson.Echo(call),
            _ => throw new InvalidOperationException(call.MessageType),
        });
        using var engine = Create(channel, new WorkerLanguageDetector(channel));

        var outcomes = await engine.TranslateBatchAsync(
            [TranslationJson.Request("Bonjour tout le monde", null), TranslationJson.Request("Unbekannte Wörter", null)],
            CancellationToken.None);

        Assert.Equal(["language.detect", "translation.translate"], channel.Calls.Select(c => c.MessageType));
        Assert.Equal("[fr>en] Bonjour tout le monde", outcomes[0].Result!.TranslatedText);
        Assert.Equal(TranslationErrorKind.LanguageUndetermined, outcomes[1].Error!.Kind);
        Assert.Equal(1, channel.MaxInFlight);
    }

    // ---- per-item errors ----

    [Theory]
    [InlineData("model_missing", TranslationErrorKind.ModelMissing)]
    [InlineData("unsupported_language_pair", TranslationErrorKind.UnsupportedLanguagePair)]
    [InlineData("same_language", TranslationErrorKind.SameLanguage)]
    [InlineData("empty_text", TranslationErrorKind.EmptyText)]
    [InlineData("text_too_long", TranslationErrorKind.TextTooLong)]
    [InlineData("translation_failed", TranslationErrorKind.Failed)]
    [InlineData("a_code_from_a_newer_worker", TranslationErrorKind.Failed)]
    [InlineData("MODEL_MISSING", TranslationErrorKind.Failed)]
    public async Task TranslateBatchAsync_MapsTheWorkersItemErrorCodes(string code, TranslationErrorKind expected)
    {
        using var engine = Create(ChannelAnswering(TranslationJson.Results(TranslationJson.Error("0", code))));

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request(Secret)], CancellationToken.None));

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Result);
        Assert.Equal(expected, outcome.Error!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error.Message));
        AssertNoSecrets(outcome.Error);
    }

    [Fact]
    public async Task TranslateBatchAsync_ModelMissing_CarriesTheRequiredPackage()
    {
        using var engine = Create(ChannelAnswering(TranslationJson.Results(TranslationJson.Error("0", "model_missing", "translate-ja_en"))));

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request("こんにちは", "ja")], CancellationToken.None));

        Assert.Equal(TranslationErrorKind.ModelMissing, outcome.Error!.Kind);
        Assert.Equal("translate-ja_en", outcome.Error.RequiredPackage);
        Assert.Contains("ja", outcome.Error.Message, StringComparison.Ordinal);
        Assert.Contains("en", outcome.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("こんにちは", outcome.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("translate-ja_en")]
    [InlineData("a.b-c_d+e")]
    public async Task TranslateBatchAsync_KeepsAPlausiblePackageName(string package)
    {
        using var engine = Create(ChannelAnswering(TranslationJson.Results(TranslationJson.Error("0", "model_missing", package))));

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request("x y z")], CancellationToken.None));

        Assert.Equal(package, outcome.Error!.RequiredPackage);
    }

    [Theory]
    [InlineData("translate-ja_en; del *")]
    [InlineData("C:\\Users\\me\\package")]
    [InlineData("")]
    public async Task TranslateBatchAsync_DropsARequiredPackageThatDoesNotLookLikeAPackageName(string package)
    {
        using var engine = Create(ChannelAnswering(TranslationJson.Results(TranslationJson.Error("0", "model_missing", package))));

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request("x y z")], CancellationToken.None));

        Assert.Equal(TranslationErrorKind.ModelMissing, outcome.Error!.Kind);
        Assert.Null(outcome.Error.RequiredPackage);
    }

    [Fact]
    public async Task TranslateBatchAsync_OneFailingItemDoesNotFailTheOthers()
    {
        var channel = ChannelAnswering(TranslationJson.Results(
            TranslationJson.Ok("0", "eins"),
            TranslationJson.Error("1", "model_missing", "translate-fr_en"),
            TranslationJson.Ok("2", "drei")));
        using var engine = Create(channel);

        var outcomes = await engine.TranslateBatchAsync([.. Requests(3)], CancellationToken.None);

        Assert.True(outcomes[0].Succeeded);
        Assert.Equal(TranslationErrorKind.ModelMissing, outcomes[1].Error!.Kind);
        Assert.True(outcomes[2].Succeeded);
        Assert.Equal("drei", outcomes[2].Result!.TranslatedText);
    }

    [Fact]
    public async Task TranslateBatchAsync_AcceptsAnEmptyTranslation()
    {
        using var engine = Create(ChannelAnswering(TranslationJson.Results(TranslationJson.Ok("0", string.Empty))));

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request("...")], CancellationToken.None));

        Assert.True(outcome.Succeeded);
        Assert.Equal(string.Empty, outcome.Result!.TranslatedText);
    }

    // ---- TranslateAsync ----

    [Fact]
    public async Task TranslateAsync_ThrowsATranslationException_WithTheItemsError()
    {
        using var engine = Create(ChannelAnswering(TranslationJson.Results(TranslationJson.Error("0", "model_missing", "translate-ja_en"))));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => engine.TranslateAsync(TranslationJson.Request(Secret, "ja"), CancellationToken.None));

        Assert.Equal(TranslationErrorKind.ModelMissing, ex.Error.Kind);
        Assert.Equal("translate-ja_en", ex.Error.RequiredPackage);
        Assert.Equal(ex.Error.Message, ex.Message);
        AssertNoSecrets(ex);
    }

    [Fact]
    public async Task TranslateAsync_FailsALocalCheck_AsATranslationException()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => engine.TranslateAsync(TranslationJson.Request("Hallo", "de", "de"), CancellationToken.None));

        Assert.Equal(TranslationErrorKind.SameLanguage, ex.Error.Kind);
        Assert.Empty(channel.Calls);
    }

    // ---- whole-request failures ----

    [Theory]
    [InlineData(WorkerErrorKind.NotConnected, null, TranslationErrorKind.WorkerUnavailable)]
    [InlineData(WorkerErrorKind.ConnectionLost, null, TranslationErrorKind.WorkerUnavailable)]
    [InlineData(WorkerErrorKind.ProcessExited, null, TranslationErrorKind.WorkerUnavailable)]
    [InlineData(WorkerErrorKind.RequestTimeout, null, TranslationErrorKind.WorkerUnavailable)]
    [InlineData(WorkerErrorKind.TooManyRequests, null, TranslationErrorKind.WorkerUnavailable)]
    [InlineData(WorkerErrorKind.HandshakeFailed, null, TranslationErrorKind.WorkerUnavailable)]
    [InlineData(WorkerErrorKind.Rejected, "engine_unavailable", TranslationErrorKind.EngineUnavailable)]
    [InlineData(WorkerErrorKind.Rejected, "invalid_request", TranslationErrorKind.Failed)]
    [InlineData(WorkerErrorKind.Rejected, "not_initialized", TranslationErrorKind.Failed)]
    [InlineData(WorkerErrorKind.Rejected, "internal_error", TranslationErrorKind.Failed)]
    [InlineData(WorkerErrorKind.Rejected, null, TranslationErrorKind.Failed)]
    [InlineData(WorkerErrorKind.ProtocolViolation, null, TranslationErrorKind.Failed)]
    public async Task TranslateBatchAsync_MapsWorkerFailures_ToATranslationException(WorkerErrorKind kind, string? code, TranslationErrorKind expected)
    {
        var failure = new WorkerException(kind, "The worker failed.", code);
        var channel = new FakeWorkerRequestChannel();
        channel.Throw(failure);
        using var engine = Create(channel);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => engine.TranslateBatchAsync([TranslationJson.Request(Secret)], CancellationToken.None));

        Assert.Equal(expected, ex.Error.Kind);
        Assert.Same(failure, ex.InnerException);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        AssertNoSecrets(ex);
    }

    [Fact]
    public async Task TranslateBatchAsync_WhenALaterChunkFails_ThrowsAndSendsNoMoreChunks()
    {
        var calls = 0;
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(call => ++calls == 2
            ? throw new WorkerException(WorkerErrorKind.ConnectionLost, "The worker connection was closed.")
            : TranslationJson.Echo(call));
        using var engine = Create(channel);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => engine.TranslateBatchAsync(Requests(20), CancellationToken.None));

        Assert.Equal(TranslationErrorKind.WorkerUnavailable, ex.Error.Kind);
        Assert.Equal(2, channel.Calls.Count); // the third chunk was never attempted
    }

    // ---- malformed responses ----

    private static string Okay(string id, string translated = SecretTranslation) =>
        $$"""{ "id": "{{id}}", "status": "ok", "translatedText": "{{translated}}", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "argos:de-en@1.9" }""";

    private static string Two(string first, string second) => "{ \"results\": [ " + first + ", " + second + " ] }";

    public static TheoryData<string?> MalformedResponses() => new()
    {
        { null },
        { "[]" },
        { "\"ok\"" },
        { "{}" },
        { """{ "results": {} }""" },
        { """{ "results": null }""" },
        { "{ \"results\": [ " + Okay("0") + " ] }" }, // too few
        { "{ \"results\": [ " + Okay("0") + ", " + Okay("1") + ", " + Okay("2") + " ] }" }, // too many
        { Two(Okay("0"), Okay("0")) }, // duplicate id
        { Two(Okay("1"), Okay("1")) },
        { Two(Okay("a"), Okay("b")) }, // not the ids that were sent
        { Two(Okay("1"), Okay("0")) }, // out of order
        { Two(Okay("0"), "1") }, // not an object
        { Two(Okay("0"), "null") },
        { Two(Okay("0"), """{ "status": "ok", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") }, // no id
        { Two(Okay("0"), """{ "id": 1, "status": "ok", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") },
        { Two(Okay("0"), """{ "id": "1", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") }, // no status
        { Two(Okay("0"), """{ "id": "1", "status": "weird", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "OK", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") }, // no translatedText
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "translatedText": null, "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "translatedText": 5, "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "m@1" }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "translatedText": "x", "targetLanguage": "en", "modelVersion": "m@1" }""") }, // no sourceLanguage
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "EN", "modelVersion": "m@1" }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en" }""") }, // no modelVersion
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "  " }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "ok", "translatedText": "x", "sourceLanguage": "de", "targetLanguage": "en", "modelVersion": "%MODEL%" }""".Replace("%MODEL%", new string('m', 300), StringComparison.Ordinal)) },
        { Two(Okay("0"), """{ "id": "1", "status": "error" }""") }, // no error object
        { Two(Okay("0"), """{ "id": "1", "status": "error", "error": null }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "error", "error": "model_missing" }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "error", "error": { "message": "no code" } }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "error", "error": { "code": "" } }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "error", "error": { "code": 7 } }""") },
        { Two(Okay("0"), """{ "id": "1", "status": "error", "error": { "code": "model_missing", "requiredPackage": 5 } }""") },
    };

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public async Task TranslateBatchAsync_RejectsAMalformedResponse_NeverInventingATranslation(string? response)
    {
        using var engine = Create(ChannelAnswering(response));

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            engine.TranslateBatchAsync([TranslationJson.Request(Secret), TranslationJson.Request("zwei")], CancellationToken.None));

        Assert.Equal(TranslationErrorKind.Failed, ex.Error.Kind);
        var inner = Assert.IsType<WorkerException>(ex.InnerException);
        Assert.Equal(WorkerErrorKind.ProtocolViolation, inner.Kind);
        AssertNoSecrets(ex);
    }

    // ---- cancellation ----

    [Fact]
    public async Task TranslateBatchAsync_WithAnAlreadyCancelledToken_ThrowsWithoutSendingAnything()
    {
        var channel = EchoChannel();
        using var engine = Create(channel);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateBatchAsync(Requests(3), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateAsync(TranslationJson.Request("Hallo"), cts.Token));

        Assert.Empty(channel.Calls);
        // The engine is not wedged: a later call with a live token works.
        Assert.True((await engine.TranslateBatchAsync(Requests(1), CancellationToken.None))[0].Succeeded);
    }

    [Fact]
    public async Task TranslateBatchAsync_CancelledWhileAChunkIsInFlight_SendsNoFurtherChunk()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(call =>
        {
            var response = TranslationJson.Echo(call);
            cts.Cancel(); // the caller moves on while the first chunk is being answered
            return response;
        });
        using var engine = Create(channel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateBatchAsync(Requests(20), cts.Token));

        Assert.Single(channel.Calls);
    }

    [Fact]
    public async Task TranslateBatchAsync_PropagatesACancellationFromTheChannel_WithoutWrappingIt()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeWorkerRequestChannel();
        channel.Handler = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(null);
        };
        using var engine = Create(channel);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateBatchAsync(Requests(20), cts.Token));

        Assert.IsNotType<TranslationException>(ex);
        Assert.Single(channel.Calls);
    }

    [Fact]
    public async Task TranslateBatchAsync_PropagatesACancellationFromTheDetector_WithoutWrappingIt()
    {
        var detector = new FakeLanguageDetector(_ => throw new OperationCanceledException());
        var channel = EchoChannel();
        using var engine = Create(channel, detector);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranslateBatchAsync([TranslationJson.Request("Hallo Welt", null)], CancellationToken.None));

        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task TranslateBatchAsync_CancelledWhileWaitingForAnotherBatch_NeverSendsItsOwnRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeWorkerRequestChannel();
        channel.Handler = async (call, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return TranslationJson.Echo(call);
        };
        using var engine = Create(channel);
        var first = engine.TranslateBatchAsync([TranslationJson.Request("erste")], CancellationToken.None);
        await started.Task;

        using var cts = new CancellationTokenSource();
        var second = engine.TranslateBatchAsync([TranslationJson.Request("zweite")], cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        Assert.Single(channel.Calls); // the second batch never reached the worker
        release.SetResult();
        Assert.Equal("[de>en] erste", (await first)[0].Result!.TranslatedText);
    }

    // ---- concurrency ----

    [Fact]
    public async Task TranslateBatchAsync_ConcurrentBatches_TakeTurns_SoOnlyOneRequestIsInFlight()
    {
        var channel = new FakeWorkerRequestChannel();
        channel.Handler = async (call, _) =>
        {
            await Task.Delay(15, CancellationToken.None);
            return TranslationJson.Echo(call);
        };
        using var engine = Create(channel);
        var batchA = Enumerable.Range(0, 10).Select(i => TranslationJson.Request($"A{i}")).ToArray();
        var batchB = Enumerable.Range(0, 10).Select(i => TranslationJson.Request($"B{i}")).ToArray();

        var outcomes = await Task.WhenAll(
            engine.TranslateBatchAsync(batchA, CancellationToken.None),
            engine.TranslateBatchAsync(batchB, CancellationToken.None));

        Assert.Equal(4, channel.Calls.Count); // 8 + 2 for each batch
        Assert.Equal(1, channel.MaxInFlight);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"[de>en] A{i}"), outcomes[0].Select(o => o.Result!.TranslatedText));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"[de>en] B{i}"), outcomes[1].Select(o => o.Result!.TranslatedText));
    }

    // ---- arguments ----

    [Fact]
    public void Constructor_RejectsANullChannel()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkerTranslationEngine(null!));
    }

    [Fact]
    public async Task Methods_RejectNullArguments()
    {
        using var engine = Create(EchoChannel());

        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.TranslateAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.TranslateBatchAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.TranslateBatchAsync([TranslationJson.Request("Hallo"), null!], CancellationToken.None));
    }
}
