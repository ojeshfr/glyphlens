using System.Text.Json;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Infrastructure.IPC;
using GlyphLens.Infrastructure.Tests.IPC;
using GlyphLens.Infrastructure.Translation;

namespace GlyphLens.Infrastructure.Tests.Translation;

/// <summary>
/// <see cref="WorkerProcessManager"/> as an <see cref="IWorkerRequestChannel"/>, against a scripted worker that speaks the real
/// protocol over a real named pipe, plus the translation clients running on top of it.
/// </summary>
public class WorkerProcessManagerRequestChannelTests
{
    private const string Secret = "Geheimer Kontoauszug 4711";
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(15);
    private static readonly string[] DeEn = ["de", "en"];

    private static WorkerManagerOptions Fast() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        InitializeTimeout = TimeSpan.FromSeconds(3),
        RequestTimeout = TimeSpan.FromSeconds(3),
        ShutdownRequestTimeout = TimeSpan.FromSeconds(1),
        GracefulExitTimeout = TimeSpan.FromSeconds(1),
        MaxRestartAttempts = 0, // a crash must stay a crash in these tests
    };

    private static WorkerProcessManager Create(ScriptedLauncher launcher, WorkerManagerOptions? options = null, RecordingLog? log = null) =>
        new(new TestPipeListenerFactory(), launcher, options ?? Fast(), log);

    private static Task<ScriptedReply?> Reply(string type, object? payload) => Task.FromResult<ScriptedReply?>(new ScriptedReply(type, payload));

    private static Task<ScriptedReply?> Silence() => Task.FromResult<ScriptedReply?>(null);

    /// <summary>Answers every request with its own payload echoed back as <c>{ "echoed": ... }</c>.</summary>
    private static Task<ScriptedReply?> Echo(WorkerMessageEnvelope request, FakeWorkerProcess process) =>
        Reply(request.MessageType + ".result", new { echoed = request.Payload });

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Long;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    // ---- the channel itself ----

    [Fact]
    public async Task TheManager_IsAWorkerRequestChannel()
    {
        await using var manager = Create(new ScriptedLauncher(Echo));

        Assert.IsAssignableFrom<IWorkerRequestChannel>(manager);
    }

    [Fact]
    public async Task RequestAsync_WhenTheWorkerWasNeverStarted_ThrowsNotConnected_AndDoesNotStartIt()
    {
        var launcher = new ScriptedLauncher(Echo);
        await using var manager = Create(launcher);

        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(WorkerErrorKind.NotConnected, ex.Kind);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Equal(WorkerStatus.Stopped, manager.Status);
    }

    [Fact]
    public async Task RequestAsync_ReturnsTheResponsePayload_SerialisedWithCamelCaseNames()
    {
        await using var manager = Create(new ScriptedLauncher(Echo));
        await manager.StartAsync(CancellationToken.None);

        var response = await manager.RequestAsync(
            "translation.translate",
            new { TargetLanguage = "en", Items = new[] { new { Id = "0", Text = "Hallo", SourceLanguage = "de" } } },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var echoed = response!.Value.GetProperty("echoed");
        Assert.Equal("en", echoed.GetProperty("targetLanguage").GetString());
        var item = echoed.GetProperty("items")[0];
        Assert.Equal("0", item.GetProperty("id").GetString());
        Assert.Equal("Hallo", item.GetProperty("text").GetString());
        Assert.Equal("de", item.GetProperty("sourceLanguage").GetString());
    }

    [Fact]
    public async Task RequestAsync_WithNoPayload_SendsNullAndReturnsNullWhenTheWorkerAnswersWithoutOne()
    {
        var seen = new List<JsonElement?>();
        var launcher = new ScriptedLauncher((request, _) =>
        {
            lock (seen)
            {
                seen.Add(request.Payload);
            }

            return Reply("models.list.result", null);
        });
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        var response = await manager.RequestAsync("models.list", null, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(response);
        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public async Task RequestAsync_AnErrorResponse_IsRejected_WithTheWorkersErrorCode()
    {
        var launcher = new ScriptedLauncher((_, _) => Reply("error", new { code = "invalid_request", message = "The request is invalid.", retryable = false }));
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal(WorkerErrorKind.Rejected, ex.Kind);
        Assert.Equal("invalid_request", ex.ErrorCode);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkerStatus.Ready, manager.Status); // a rejected request is not a failure of the worker
    }

    [Fact]
    public async Task RequestAsync_WhenTheWorkerDoesNotAnswer_TimesOut_AndTheConnectionStaysUsable()
    {
        var first = true;
        var launcher = new ScriptedLauncher((request, process) =>
        {
            if (first)
            {
                first = false;
                return Silence();
            }

            return Echo(request, process);
        });
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.Equal(WorkerErrorKind.RequestTimeout, ex.Kind);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkerStatus.Ready, manager.Status);
        var response = await manager.RequestAsync("translation.translate", new { text = "again" }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("again", response!.Value.GetProperty("echoed").GetProperty("text").GetString());
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task RequestAsync_TimeoutsDoNotMakeTheWorkerCountAsHung()
    {
        // A worker that is busy translating is not hung: only health timeouts count towards restarting it.
        var launcher = new ScriptedLauncher((_, _) => Silence());
        var options = Fast() with { MaxConsecutiveHealthTimeouts = 1, MaxRestartAttempts = 3, RestartBackoffInitial = TimeSpan.FromMilliseconds(20) };
        await using var manager = Create(launcher, options);
        await manager.StartAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<WorkerException>(() =>
                manager.RequestAsync("translation.translate", new { text = "x" }, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        }

        await Task.Delay(300);
        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task RequestAsync_WhenTheWorkerDiesMidRequest_ThrowsConnectionLost_AndLaterRequestsAreNotConnected()
    {
        var launcher = new ScriptedLauncher((_, process) =>
        {
            process.Crash();
            return Silence();
        });
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Equal(WorkerErrorKind.ConnectionLost, ex.Kind);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.True(await UntilAsync(() => manager.Status != WorkerStatus.Ready));

        var later = await Assert.ThrowsAsync<WorkerException>(() =>
            manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(WorkerErrorKind.NotConnected, later.Kind);
        Assert.Equal(1, launcher.LaunchCount); // RequestAsync never starts a worker
    }

    [Fact]
    public async Task RequestAsync_AfterStop_ThrowsNotConnected()
    {
        await using var manager = Create(new ScriptedLauncher(Echo));
        await manager.StartAsync(CancellationToken.None);
        await manager.StopAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            manager.RequestAsync("models.list", null, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(WorkerErrorKind.NotConnected, ex.Kind);
    }

    [Fact]
    public async Task RequestAsync_WhenCancelled_ThrowsOperationCanceled_AndTheConnectionStaysUsable()
    {
        var first = true;
        var launcher = new ScriptedLauncher((request, process) =>
        {
            if (first)
            {
                first = false;
                return Silence();
            }

            return Echo(request, process);
        });
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(30), cts.Token));

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        var response = await manager.RequestAsync("translation.translate", new { text = "again" }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(response);
    }

    [Fact]
    public async Task RequestAsync_WithAnAlreadyCancelledToken_ThrowsOperationCanceled()
    {
        await using var manager = Create(new ScriptedLauncher(Echo));
        await manager.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(5), cts.Token));
    }

    [Theory]
    [InlineData("worker.initialize")]
    [InlineData("worker.shutdown")]
    public async Task RequestAsync_RefusesTheWorkerLifecycleMessages(string messageType)
    {
        var launcher = new ScriptedLauncher(Echo);
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.RequestAsync(messageType, null, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(WorkerStatus.Ready, manager.Status);
        Assert.False(launcher.Processes[0].Exited.IsCompleted);
    }

    [Fact]
    public async Task RequestAsync_ValidatesItsArguments()
    {
        await using var manager = Create(new ScriptedLauncher(Echo));
        await manager.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.RequestAsync(null!, null, TimeSpan.FromSeconds(1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.RequestAsync(" ", null, TimeSpan.FromSeconds(1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.RequestAsync("models.list", null, TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.RequestAsync("models.list", null, TimeSpan.FromSeconds(-1), CancellationToken.None));
    }

    [Fact]
    public async Task RequestAsync_NeverPutsThePayloadInTheLog()
    {
        var log = new RecordingLog();
        var first = true;
        var launcher = new ScriptedLauncher((request, process) =>
        {
            if (first)
            {
                first = false;
                return Reply("error", new { code = "engine_unavailable", message = "not available", retryable = false });
            }

            return Echo(request, process);
        });
        await using var manager = Create(launcher, log: log);
        await manager.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<WorkerException>(() => manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(5), CancellationToken.None));
        await manager.RequestAsync("translation.translate", new { text = Secret }, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Contains(log.Requests, r => r.MessageType == "translation.translate" && r.Outcome == "ok");
        Assert.Contains(log.Requests, r => r.MessageType == "translation.translate" && r.ErrorCode == "engine_unavailable");
        Assert.DoesNotContain(log.Requests, r => r.ToString().Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Events, e => e.Contains(Secret, StringComparison.Ordinal));
    }

    // ---- the translation clients on the real channel ----

    private static ScriptedLauncher TranslatingWorker(List<string> requestTypes) => new((request, _) =>
    {
        lock (requestTypes)
        {
            requestTypes.Add(request.MessageType);
        }

        var call = new RecordedCall(request.MessageType, request.Payload, TimeSpan.Zero);
        return request.MessageType switch
        {
            "translation.translate" => Reply("translation.translate.result", TranslationJson.Echo(call)),
            "language.detect" => Reply("language.detect.result", TranslationJson.DetectionResults(
                [.. call.SentTexts().Select(t => t.StartsWith("Bonjour", StringComparison.Ordinal) ? TranslationJson.Detection("fr", 0.97) : TranslationJson.NoDetection())])),
            "models.list" => Reply("models.list.result", new
            {
                engine = "argos-translate",
                state = "installed",
                storagePath = "C:\\models",
                packages = new[] { new { from = "de", to = "en", fromName = "German", toName = "English", version = "1.9", packageName = "translate-de_en" } },
                problems = Array.Empty<string>(),
            }),
            "models.status" => Reply("models.status.result", new { source = "de", target = "en", available = true, route = DeEn, modelVersion = "argos:de-en@1.9", missingPackage = (string?)null, issue = "none" }),
            _ => Reply("error", new { code = "engine_unavailable", message = "not implemented", retryable = false }),
        };
    });

    [Fact]
    public async Task TheEngine_TranslatesTwentyItemsInThreeRequests_OverTheRealPipe()
    {
        var types = new List<string>();
        await using var manager = Create(TranslatingWorker(types));
        await manager.StartAsync(CancellationToken.None);
        using var engine = new WorkerTranslationEngine(manager);

        var outcomes = await engine.TranslateBatchAsync(
            [.. Enumerable.Range(0, 20).Select(i => TranslationJson.Request($"Text {i}"))],
            CancellationToken.None);

        Assert.Equal(["translation.translate", "translation.translate", "translation.translate"], types);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal($"[de>en] Text {i}", outcomes[i].Result!.TranslatedText);
            Assert.Equal("argos:de-en@1.9", outcomes[i].Result!.ModelIdentifier);
        }
    }

    [Fact]
    public async Task TheEngine_WithTheDetector_IdentifiesTheSourceLanguageOverTheRealPipe()
    {
        var types = new List<string>();
        await using var manager = Create(TranslatingWorker(types));
        await manager.StartAsync(CancellationToken.None);
        using var engine = new WorkerTranslationEngine(manager, new WorkerLanguageDetector(manager));

        var outcome = Assert.Single(await engine.TranslateBatchAsync([TranslationJson.Request("Bonjour tout le monde", null)], CancellationToken.None));

        Assert.Equal(["language.detect", "translation.translate"], types);
        Assert.Equal("[fr>en] Bonjour tout le monde", outcome.Result!.TranslatedText);
    }

    [Fact]
    public async Task TheEngine_WhenTheWorkerIsNotRunning_FailsWithWorkerUnavailable()
    {
        var launcher = new ScriptedLauncher(Echo);
        await using var manager = Create(launcher);
        using var engine = new WorkerTranslationEngine(manager);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => engine.TranslateAsync(TranslationJson.Request(Secret), CancellationToken.None));

        Assert.Equal(TranslationErrorKind.WorkerUnavailable, ex.Error.Kind);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task TheEngine_WhenTheWorkerAnswersEngineUnavailable_FailsWithEngineUnavailable()
    {
        var launcher = new ScriptedLauncher((_, _) => Reply("error", new { code = "engine_unavailable", message = "Argos is not installed.", retryable = false }));
        await using var manager = Create(launcher);
        await manager.StartAsync(CancellationToken.None);
        using var engine = new WorkerTranslationEngine(manager);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => engine.TranslateAsync(TranslationJson.Request("Hallo"), CancellationToken.None));

        Assert.Equal(TranslationErrorKind.EngineUnavailable, ex.Error.Kind);
    }

    [Fact]
    public async Task TheCatalog_ReadsTheModelsAndRouteStatus_OverTheRealPipe()
    {
        var types = new List<string>();
        await using var manager = Create(TranslatingWorker(types));
        await manager.StartAsync(CancellationToken.None);
        var catalog = new WorkerModelCatalog(manager);

        var models = await catalog.GetCatalogAsync(CancellationToken.None);
        var route = await catalog.GetRouteStatusAsync("de", "en", CancellationToken.None);

        Assert.Equal("translate-de_en", Assert.Single(models.Packages).PackageName);
        Assert.True(route.Available);
        Assert.Equal("argos:de-en@1.9", route.ModelVersion);
        Assert.Equal(["models.list", "models.status"], types);
    }

    [Fact]
    public async Task TheDetector_IdentifiesLanguagesOverTheRealPipe()
    {
        var types = new List<string>();
        await using var manager = Create(TranslatingWorker(types));
        await manager.StartAsync(CancellationToken.None);
        var detector = new WorkerLanguageDetector(manager);

        var results = await detector.DetectAsync(["Bonjour tout le monde", "12345"], CancellationToken.None);

        Assert.Equal("fr", results[0].Language);
        Assert.Equal(0.97, results[0].Confidence);
        Assert.Equal(LanguageDetectionIssue.NoText, results[1].Issue);
        Assert.Equal(["language.detect"], types);
    }
}
