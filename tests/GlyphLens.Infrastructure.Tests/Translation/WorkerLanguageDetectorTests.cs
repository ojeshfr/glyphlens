using System.Globalization;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.Translation;

namespace GlyphLens.Infrastructure.Tests.Translation;

public class WorkerLanguageDetectorTests
{
    private const string Secret = "Geheimer Kontoauszug 4711";

    private static WorkerLanguageDetector Create(FakeWorkerRequestChannel channel, TranslationClientOptions? options = null) =>
        new(channel, options);

    private static FakeWorkerRequestChannel ChannelAnswering(Func<RecordedCall, object?> respond)
    {
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(respond);
        return channel;
    }

    /// <summary>A worker that identifies every text as German and reports the number found in "Wort nummer N" as its confidence.</summary>
    private static object NumberedDetection(RecordedCall call) =>
        TranslationJson.DetectionResults([.. call.SentTexts().Select(text =>
            TranslationJson.Detection("de", double.Parse(text.Split(' ')[^1], CultureInfo.InvariantCulture) / 1000.0))]);

    private static string Numbered(int i) => "Wort nummer " + i.ToString(CultureInfo.InvariantCulture);

    // ---- happy path ----

    [Fact]
    public async Task DetectAsync_SendsTheTextsInOneRequest_AndReturnsTheResultsInOrder()
    {
        var channel = ChannelAnswering(_ => new
        {
            detector = "lingua 2.x",
            results = new object[]
            {
                new { language = "de", confidence = 0.98, alternatives = new[] { new { language = "nl", confidence = 0.01 } }, issue = "none" },
                new { language = "fr", confidence = (double?)null, alternatives = Array.Empty<object>(), issue = "none" },
            },
        });

        var results = await Create(channel).DetectAsync(["Hallo Welt wie geht es", "Bonjour tout le monde"], CancellationToken.None);

        var call = Assert.Single(channel.Calls);
        Assert.Equal("language.detect", call.MessageType);
        Assert.Equal(TimeSpan.FromSeconds(15), call.Timeout);
        Assert.Equal(["texts"], call.Json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Hallo Welt wie geht es", "Bonjour tout le monde"], call.SentTexts());

        Assert.Equal(2, results.Count);
        Assert.Equal("de", results[0].Language);
        Assert.Equal(0.98, results[0].Confidence);
        Assert.Equal(LanguageDetectionIssue.None, results[0].Issue);
        var alternative = Assert.Single(results[0].Alternatives);
        Assert.Equal("nl", alternative.Language);
        Assert.Equal(0.01, alternative.Confidence);
        Assert.Equal("fr", results[1].Language);
        Assert.Null(results[1].Confidence); // the detector reported none; nothing is invented
        Assert.Empty(results[1].Alternatives);
    }

    [Fact]
    public async Task DetectAsync_UsesTheConfiguredTimeout()
    {
        var channel = ChannelAnswering(c => NumberedDetection(c));

        await Create(channel, new TranslationClientOptions { DetectTimeout = TimeSpan.FromSeconds(3) })
            .DetectAsync([Numbered(1)], CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(channel.Calls).Timeout);
    }

    [Fact]
    public async Task DetectAsync_ReportsAnUndeterminedAnswerFromTheWorker()
    {
        var channel = ChannelAnswering(_ => TranslationJson.DetectionResults(
            TranslationJson.NoDetection("unknown"),
            TranslationJson.NoDetection("too_short")));

        var results = await Create(channel).DetectAsync(["Some ambiguous words", "tiny"], CancellationToken.None);

        Assert.Null(results[0].Language);
        Assert.Equal(LanguageDetectionIssue.Unknown, results[0].Issue);
        Assert.Equal(LanguageDetectionIssue.TooShort, results[1].Issue);
    }

    [Fact]
    public async Task DetectAsync_KeepsTheAlternativesOfAnUndeterminedAnswer_AndDropsItsConfidence()
    {
        var channel = ChannelAnswering(_ => """
            { "results": [ { "language": null, "confidence": 0.4, "issue": "unknown",
                             "alternatives": [ { "language": "de", "confidence": 0.4 }, { "language": "nl", "confidence": 0.3 } ] } ] }
            """);

        var result = Assert.Single(await Create(channel).DetectAsync(["Some ambiguous words"], CancellationToken.None));

        Assert.Null(result.Language);
        Assert.Null(result.Confidence);
        Assert.Equal(LanguageDetectionIssue.Unknown, result.Issue);
        Assert.Equal(["de", "nl"], result.Alternatives.Select(a => a.Language));
    }

    [Fact]
    public async Task DetectAsync_AcceptsAMissingAlternativesList_AndIgnoresUnknownFields()
    {
        var channel = ChannelAnswering(_ => """
            { "results": [ { "language": "de", "confidence": 0.9, "issue": "none", "futureField": [1, 2, 3] } ], "other": true }
            """);

        var result = Assert.Single(await Create(channel).DetectAsync(["Guten Morgen"], CancellationToken.None));

        Assert.Equal("de", result.Language);
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public async Task DetectAsync_WithNoTexts_ReturnsNothingWithoutContactingTheWorker()
    {
        var channel = new FakeWorkerRequestChannel();

        var results = await Create(channel).DetectAsync([], CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(channel.Calls);
    }

    // ---- local short-circuit ----

    [Theory]
    [InlineData("好")]
    [InlineData("谢谢")]
    [InlineData("OK好")]
    [InlineData("はい")]
    [InlineData("네")]
    public async Task DetectAsync_AsksTheWorkerAboutShortCjkText_BecauseOneCharacterIsAWholeWord(string text)
    {
        var channel = ChannelAnswering(_ => TranslationJson.DetectionResults([TranslationJson.Detection("zh", null)]));

        var result = Assert.Single(await Create(channel).DetectAsync([text], CancellationToken.None));

        Assert.Equal([text], Assert.Single(channel.Calls).SentTexts());
        Assert.Equal("zh", result.Language);
    }

    [Theory]
    [InlineData("", LanguageDetectionIssue.NoText)]
    [InlineData("   \t\r\n ", LanguageDetectionIssue.NoText)]
    [InlineData("12345 !?", LanguageDetectionIssue.NoText)]
    [InlineData("---", LanguageDetectionIssue.NoText)]
    [InlineData("a", LanguageDetectionIssue.TooShort)]
    [InlineData("ab", LanguageDetectionIssue.TooShort)]
    [InlineData("a1b2", LanguageDetectionIssue.TooShort)]
    [InlineData("1 a 2 b 3", LanguageDetectionIssue.TooShort)]
    public async Task DetectAsync_AnswersTextThatCannotBeIdentifiedLocally_WithoutAWorkerRequest(string text, LanguageDetectionIssue expected)
    {
        var channel = new FakeWorkerRequestChannel();

        var result = Assert.Single(await Create(channel).DetectAsync([text], CancellationToken.None));

        Assert.Empty(channel.Calls);
        Assert.Null(result.Language);
        Assert.Null(result.Confidence);
        Assert.Equal(expected, result.Issue);
    }

    [Fact]
    public async Task DetectAsync_SendsTextWithThreeLettersToTheWorker()
    {
        var channel = ChannelAnswering(_ => TranslationJson.DetectionResults(TranslationJson.Detection("de")));

        await Create(channel).DetectAsync(["abc"], CancellationToken.None);

        Assert.Equal(["abc"], Assert.Single(channel.Calls).SentTexts());
    }

    [Fact]
    public async Task DetectAsync_KeepsTheOrderWhenLocalAndRemoteTextsAreMixed()
    {
        var channel = ChannelAnswering(call => TranslationJson.DetectionResults(
            [.. call.SentTexts().Select(t => TranslationJson.Detection(t.StartsWith("Hallo", StringComparison.Ordinal) ? "de" : "fr"))]));

        var results = await Create(channel).DetectAsync(["Hallo Welt", "42", "Bonjour tout le monde", "", "ab"], CancellationToken.None);

        Assert.Equal(["Hallo Welt", "Bonjour tout le monde"], Assert.Single(channel.Calls).SentTexts());
        Assert.Equal(5, results.Count);
        Assert.Equal("de", results[0].Language);
        Assert.Equal(LanguageDetectionIssue.NoText, results[1].Issue);
        Assert.Equal("fr", results[2].Language);
        Assert.Equal(LanguageDetectionIssue.NoText, results[3].Issue);
        Assert.Equal(LanguageDetectionIssue.TooShort, results[4].Issue);
    }

    // ---- batching ----

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(64, new[] { 64 })]
    [InlineData(65, new[] { 64, 1 })]
    [InlineData(128, new[] { 64, 64 })]
    [InlineData(150, new[] { 64, 64, 22 })]
    public async Task DetectAsync_SendsAtMost64TextsPerRequest_AndKeepsEveryResultAtItsInput(int count, int[] expectedBatchSizes)
    {
        var channel = ChannelAnswering(c => NumberedDetection(c));
        var texts = Enumerable.Range(0, count).Select(Numbered).ToArray();

        var results = await Create(channel).DetectAsync(texts, CancellationToken.None);

        Assert.Equal(expectedBatchSizes, channel.Calls.Select(c => c.SentTexts().Length));
        Assert.Equal(1, channel.MaxInFlight);
        Assert.Equal(count, results.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i / 1000.0, results[i].Confidence);
        }
    }

    [Fact]
    public async Task DetectAsync_BatchesOnlyTheTextsThatNeedTheWorker()
    {
        var channel = ChannelAnswering(c => NumberedDetection(c));
        var texts = new List<string>();
        for (var i = 0; i < 70; i++)
        {
            texts.Add(Numbered(i));
            texts.Add(i.ToString(CultureInfo.InvariantCulture)); // no letters: answered locally
        }

        var results = await Create(channel).DetectAsync(texts, CancellationToken.None);

        Assert.Equal([64, 6], channel.Calls.Select(c => c.SentTexts().Length));
        Assert.Equal(140, results.Count);
        for (var i = 0; i < 70; i++)
        {
            Assert.Equal(i / 1000.0, results[2 * i].Confidence);
            Assert.Equal(LanguageDetectionIssue.NoText, results[(2 * i) + 1].Issue);
        }
    }

    // ---- long text ----

    [Fact]
    public async Task DetectAsync_CutsATextLongerThanTheLimit()
    {
        var channel = ChannelAnswering(_ => TranslationJson.DetectionResults(TranslationJson.Detection("de")));

        await Create(channel).DetectAsync([new string('a', 5000)], CancellationToken.None);

        Assert.Equal(4096, Assert.Single(channel.Calls).SentTexts()[0].Length);
    }

    [Fact]
    public async Task DetectAsync_NeverCutsInTheMiddleOfASurrogatePair()
    {
        var channel = ChannelAnswering(_ => TranslationJson.DetectionResults(TranslationJson.Detection("de")));
        var text = new string('a', 4095) + "\U0001F600" + "tail";

        await Create(channel).DetectAsync([text], CancellationToken.None);

        var sent = Assert.Single(channel.Calls).SentTexts()[0];
        Assert.Equal(4095, sent.Length);
        Assert.False(char.IsHighSurrogate(sent[^1]));
    }

    [Fact]
    public async Task DetectAsync_HonoursAConfiguredTextLimit()
    {
        var channel = ChannelAnswering(_ => TranslationJson.DetectionResults(TranslationJson.Detection("de")));

        await Create(channel, new TranslationClientOptions { MaxTextLength = 10 }).DetectAsync(["Hallo Welt, wie geht es dir"], CancellationToken.None);

        Assert.Equal("Hallo Welt", Assert.Single(channel.Calls).SentTexts()[0]);
    }

    // ---- failures of the worker ----

    [Theory]
    [InlineData(WorkerErrorKind.NotConnected, null)]
    [InlineData(WorkerErrorKind.RequestTimeout, null)]
    [InlineData(WorkerErrorKind.ConnectionLost, null)]
    [InlineData(WorkerErrorKind.Rejected, "engine_unavailable")]
    [InlineData(WorkerErrorKind.Rejected, "invalid_request")]
    public async Task DetectAsync_LetsWorkerFailuresThrough_AndStopsAfterTheFailedBatch(WorkerErrorKind kind, string? code)
    {
        var failure = new WorkerException(kind, "The worker failed.", code);
        var channel = new FakeWorkerRequestChannel();
        channel.Throw(failure);
        var texts = Enumerable.Range(0, 100).Select(Numbered).ToArray();

        var ex = await Assert.ThrowsAsync<WorkerException>(() => Create(channel).DetectAsync(texts, CancellationToken.None));

        Assert.Same(failure, ex);
        Assert.Single(channel.Calls); // the second batch is not attempted
    }

    // ---- cancellation ----

    [Fact]
    public async Task DetectAsync_WithAnAlreadyCancelledToken_ThrowsWithoutSendingAnything()
    {
        var channel = ChannelAnswering(c => NumberedDetection(c));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(channel).DetectAsync([Numbered(1)], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(channel).DetectAsync(["123"], cts.Token));

        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task DetectAsync_CancelledBetweenBatches_SendsNoFurtherBatch()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeWorkerRequestChannel();
        channel.Respond(call =>
        {
            var response = NumberedDetection(call);
            cts.Cancel(); // the caller gives up while the first batch is being answered
            return response;
        });
        var texts = Enumerable.Range(0, 150).Select(Numbered).ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(channel).DetectAsync(texts, cts.Token));

        Assert.Single(channel.Calls);
    }

    [Fact]
    public async Task DetectAsync_PropagatesACancellationFromTheChannelUnchanged()
    {
        using var cts = new CancellationTokenSource();
        var channel = new FakeWorkerRequestChannel();
        channel.Handler = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(null);
        };

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(channel).DetectAsync([Numbered(1)], cts.Token));

        Assert.IsNotType<WorkerException>(ex);
    }

    // ---- strict validation of the response ----

    public static TheoryData<string?> MalformedResponses() => new()
    {
        { null }, // no payload at all
        { "[]" },
        { "\"results\"" },
        { "{}" },
        { """{ "results": {} }""" },
        { """{ "results": null }""" },
        { """{ "results": [] }""" }, // too few
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "none" }, { "language": "de", "confidence": 0.9, "issue": "none" } ] }""" }, // too many
        { """{ "results": [ 1 ] }""" },
        { """{ "results": [ null ] }""" },
        { """{ "results": [ { "confidence": 0.9, "issue": "none" } ] }""" }, // no language field: not "none given", just absent
        { """{ "results": [ { "language": "EN", "confidence": 0.9, "issue": "none" } ] }""" },
        { """{ "results": [ { "language": "english", "confidence": 0.9, "issue": "none" } ] }""" },
        { """{ "results": [ { "language": "d", "confidence": 0.9, "issue": "none" } ] }""" },
        { """{ "results": [ { "language": 5, "confidence": 0.9, "issue": "none" } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 1.5, "issue": "none" } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": -0.1, "issue": "none" } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": "0.9", "issue": "none" } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": true, "issue": "none" } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9 } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "bogus" } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": 0 } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": null } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "too_short" } ] }""" }, // a language and a problem
        { """{ "results": [ { "language": null, "confidence": null, "issue": "none" } ] }""" }, // no language and no reason
        { """{ "results": [ { "language": null, "confidence": 2, "issue": "unknown" } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "none", "alternatives": {} } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "none", "alternatives": [ 1 ] } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "none", "alternatives": [ { "language": "XX", "confidence": 0.1 } ] } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "none", "alternatives": [ { "language": "nl" } ] } ] }""" },
        { """{ "results": [ { "language": "de", "confidence": 0.9, "issue": "none", "alternatives": [ { "language": "nl", "confidence": 1.1 } ] } ] }""" },
    };

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public async Task DetectAsync_RejectsAMalformedResponse_AsAProtocolViolation_NeverInventingAResult(string? response)
    {
        var channel = ChannelAnswering(_ => response);

        var ex = await Assert.ThrowsAsync<WorkerException>(() => Create(channel).DetectAsync([Secret], CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ProtocolViolation, ex.Kind);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectAsync_RejectsAResponseWithTheWrongCountForALaterBatch()
    {
        var calls = 0;
        var channel = ChannelAnswering(call => ++calls == 1
            ? NumberedDetection(call)
            : TranslationJson.DetectionResults(TranslationJson.Detection("de"))); // the second batch has 36 texts
        var texts = Enumerable.Range(0, 100).Select(Numbered).ToArray();

        var ex = await Assert.ThrowsAsync<WorkerException>(() => Create(channel).DetectAsync(texts, CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ProtocolViolation, ex.Kind);
    }

    // ---- arguments ----

    [Fact]
    public void Constructor_RejectsANullChannel()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkerLanguageDetector(null!));
    }

    [Fact]
    public async Task DetectAsync_RejectsNullArguments()
    {
        var detector = Create(new FakeWorkerRequestChannel());

        await Assert.ThrowsAsync<ArgumentNullException>(() => detector.DetectAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => detector.DetectAsync(["fine text here", null!], CancellationToken.None));
    }
}
