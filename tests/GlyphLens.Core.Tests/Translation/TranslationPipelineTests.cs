using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Translation;

namespace GlyphLens.Core.Tests.Translation;

public sealed class TranslationPipelineTests
{
    private const string ModelVersion = "argos:de-en@1.9";

    private static ScreenTextRegion Region(string text, int x = 10, int y = 20, int width = 100, int height = 18) =>
        new(Guid.NewGuid(), text, x, y, width, height, DetectionSource.Ocr, null, null, DateTimeOffset.UnixEpoch);

    private static (TranslationPipeline Pipeline, Fakes Fakes) Create(Action<Fakes>? configure = null, TranslationPipelineOptions? options = null)
    {
        var fakes = new Fakes();
        configure?.Invoke(fakes);
        var pipeline = new TranslationPipeline(fakes.Detector, fakes.Catalog, fakes.Engine, fakes.Cache, options);
        return (pipeline, fakes);
    }

    [Fact]
    public async Task GermanRegion_IsDetectedAndTranslated_KeepingIdentityAndCoordinates()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo Welt", "de", 0.98));
        var region = Region("Hallo Welt", x: 42, y: 77, width: 160, height: 22);

        var pass = await pipeline.TranslateAsync([region], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.Translated, item.Status);
        Assert.Equal("[en] Hallo Welt", item.TranslatedText);
        Assert.Equal("de", item.SourceLanguage);
        Assert.False(item.SourceChosenByUser);
        Assert.Equal(0.98, item.DetectionConfidence);
        Assert.Equal(ModelVersion, item.ModelVersion);
        Assert.False(item.FromCache);
        Assert.Same(region, item.Region);
        Assert.Equal(region.Id, item.RegionId);
        Assert.Equal((42, 77, 160, 22), (item.Region.X, item.Region.Y, item.Region.Width, item.Region.Height));
        Assert.Equal("Hallo Welt", item.Region.Text);
        Assert.Equal(1, pass.TextsDetected);
        Assert.Equal(1, pass.TextsTranslated);
        Assert.Equal(1, fakes.Engine.TextsTranslated);
    }

    [Fact]
    public async Task Results_AreInInputOrder_WithEachRegionMappedToItsOwnTranslation()
    {
        var (pipeline, _) = Create(f =>
        {
            f.Detector.Set("Guten Morgen", "de", 0.9);
            f.Detector.Set("Bonjour le monde", "fr", 0.9);
            f.Catalog.AddRoute("fr", "en", "argos:fr-en@1.0");
        });
        var regions = new[] { Region("Guten Morgen"), Region("Bonjour le monde"), Region("Guten Morgen", x: 400) };

        var pass = await pipeline.TranslateAsync(regions, TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(3, pass.Regions.Count);
        Assert.Equal(regions.Select(r => r.Id), pass.Regions.Select(r => r.RegionId));
        Assert.Equal(["[en] Guten Morgen", "[en] Bonjour le monde", "[en] Guten Morgen"], pass.Regions.Select(r => r.TranslatedText));
        Assert.Equal(["de", "fr", "de"], pass.Regions.Select(r => r.SourceLanguage));
        Assert.Equal(400, pass.Regions[2].Region.X);
    }

    [Fact]
    public async Task IdenticalTexts_AreDetectedAndTranslatedOnce()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo", "de", 0.95));

        var pass = await pipeline.TranslateAsync(
            [Region("Hallo"), Region("Hallo", x: 5), Region("  Hallo  ", x: 9)],
            TranslationSelection.AutoToEnglish,
            CancellationToken.None);

        Assert.All(pass.Regions, r => Assert.Equal("[en] Hallo", r.TranslatedText));
        Assert.Equal(1, pass.TextsDetected);
        Assert.Equal(1, pass.TextsTranslated);
        Assert.Equal(1, fakes.Engine.TextsTranslated);
        Assert.Equal(1, fakes.Detector.TextsDetected);
    }

    [Fact]
    public async Task SecondPassWithSameText_IsAnsweredFromCache_WithoutDetectingOrTranslating()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo Welt", "de", 0.98));
        await pipeline.TranslateAsync([Region("Hallo Welt")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var second = await pipeline.TranslateAsync([Region("Hallo Welt", x: 300)], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(second.Regions);
        Assert.Equal(RegionTranslationStatus.Translated, item.Status);
        Assert.True(item.FromCache);
        Assert.Equal("[en] Hallo Welt", item.TranslatedText);
        Assert.Equal(1, second.CacheHits);
        Assert.Equal(0, second.TextsTranslated);
        Assert.Equal(0, second.TextsDetected);
        Assert.Equal(1, fakes.Engine.TextsTranslated);
        Assert.Equal(1, fakes.Detector.TextsDetected);
        Assert.Equal(1, fakes.Engine.Calls);
    }

    [Fact]
    public async Task WhitespaceDifferences_StillHitTheCache()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo Welt", "de", 0.98));
        await pipeline.TranslateAsync([Region("Hallo Welt")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var second = await pipeline.TranslateAsync([Region("Hallo   Welt\r\n")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.True(Assert.Single(second.Regions).FromCache);
        Assert.Equal(1, fakes.Engine.TextsTranslated);
    }

    [Fact]
    public async Task InstallingAModel_ChangesTheCacheKey_SoOldTranslationsAreNotReused()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo Welt", "de", 0.98));
        await pipeline.TranslateAsync([Region("Hallo Welt")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        fakes.Catalog.AddRoute("de", "en", "argos:de-en@2.0");
        fakes.Catalog.RaiseChanged();
        var second = await pipeline.TranslateAsync([Region("Hallo Welt")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(second.Regions);
        Assert.False(item.FromCache);
        Assert.Equal("argos:de-en@2.0", item.ModelVersion);
        Assert.Equal(2, fakes.Engine.TextsTranslated);
    }

    [Fact]
    public async Task EnglishText_IsNotTranslated_AndTheEngineIsNeverCalled()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hello world", "en", 0.99));

        var pass = await pipeline.TranslateAsync([Region("Hello world")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.AlreadyTarget, item.Status);
        Assert.Null(item.TranslatedText);
        Assert.Equal("en", item.SourceLanguage);
        Assert.Equal(0, fakes.Engine.Calls);
        Assert.Equal(0, fakes.Catalog.RouteQueries);
    }

    [Fact]
    public async Task ManualSourceLanguage_SkipsDetection()
    {
        var (pipeline, fakes) = Create();

        var pass = await pipeline.TranslateAsync([Region("Hallo")], new TranslationSelection("de", "en"), CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.Translated, item.Status);
        Assert.True(item.SourceChosenByUser);
        Assert.Equal("de", item.SourceLanguage);
        Assert.Null(item.DetectionConfidence);
        Assert.Equal(0, fakes.Detector.Calls);
        Assert.Equal(0, pass.TextsDetected);
    }

    [Fact]
    public async Task ManualSourceEqualToTarget_IsNotTranslated()
    {
        var (pipeline, fakes) = Create();

        var pass = await pipeline.TranslateAsync([Region("Hello")], new TranslationSelection("en", "en"), CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.AlreadyTarget, Assert.Single(pass.Regions).Status);
        Assert.Equal(0, fakes.Engine.Calls);
    }

    [Fact]
    public async Task LowConfidenceDetection_IsUncertain_AndNotTranslated()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set(
            "Mama mia",
            "it",
            0.41,
            new LanguageCandidate("es", 0.35),
            new LanguageCandidate("pt", 0.2)));

        var pass = await pipeline.TranslateAsync([Region("Mama mia")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.LanguageUncertain, item.Status);
        Assert.Null(item.TranslatedText);
        Assert.Null(item.SourceLanguage);
        Assert.Equal(["it", "es", "pt"], item.Alternatives.Select(a => a.Language));
        Assert.Contains("Choose the source language", item.Message, StringComparison.Ordinal);
        Assert.Equal(0, fakes.Engine.Calls);
    }

    [Fact]
    public async Task UncertainText_CanBeTranslatedByChoosingTheLanguageManually()
    {
        var (pipeline, _) = Create(f => f.Detector.Set("Mama mia", "it", 0.41));
        var regions = new[] { Region("Mama mia") };

        var automatic = await pipeline.TranslateAsync(regions, TranslationSelection.AutoToEnglish, CancellationToken.None);
        var manual = await pipeline.TranslateAsync(regions, new TranslationSelection("de", "en"), CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.LanguageUncertain, automatic.Regions[0].Status);
        Assert.Equal(RegionTranslationStatus.Translated, manual.Regions[0].Status);
    }

    [Theory]
    [InlineData(LanguageDetectionIssue.TooShort)]
    [InlineData(LanguageDetectionIssue.Unknown)]
    public async Task UndeterminedDetection_IsUncertain(LanguageDetectionIssue issue)
    {
        var (pipeline, fakes) = Create(f => f.Detector.SetUndetermined("Hi", issue));

        var pass = await pipeline.TranslateAsync([Region("Hi")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.LanguageUncertain, Assert.Single(pass.Regions).Status);
        Assert.Equal(0, fakes.Engine.Calls);
    }

    [Theory]
    [InlineData("12:30")]
    [InlineData("100 %")]
    [InlineData("© • —")]
    public async Task TextWithoutLetters_IsNoText_AndNeverSentToTheWorker(string text)
    {
        var (pipeline, fakes) = Create();

        var pass = await pipeline.TranslateAsync([Region(text)], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.NoText, Assert.Single(pass.Regions).Status);
        Assert.Equal(0, fakes.Detector.Calls);
        Assert.Equal(0, fakes.Engine.Calls);
    }

    [Fact]
    public async Task MissingModel_ReportsTheRequiredPackage_AndInventsNoTranslation()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("こんにちは世界", "ja", 0.99));

        var pass = await pipeline.TranslateAsync([Region("こんにちは世界")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.ModelMissing, item.Status);
        Assert.Null(item.TranslatedText);
        Assert.Equal("translate-ja_en", item.RequiredPackage);
        Assert.Contains("translate-ja_en", item.Message, StringComparison.Ordinal);
        Assert.Equal("ja", item.SourceLanguage);
        Assert.Equal(0, fakes.Engine.Calls);
    }

    [Fact]
    public async Task UnsupportedPair_IsReportedAsUnsupported()
    {
        var (pipeline, _) = Create(f =>
        {
            f.Detector.Set("Qapla'", "tlh", 0.9);
            f.Catalog.SetIssue("tlh", "en", RouteIssue.UnsupportedPair);
        });

        var pass = await pipeline.TranslateAsync([Region("Qapla'")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.UnsupportedLanguage, item.Status);
        Assert.Null(item.TranslatedText);
    }

    [Fact]
    public async Task EngineUnavailableRoute_IsReported_AndNotRemembered()
    {
        var (pipeline, fakes) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Catalog.SetIssue("de", "en", RouteIssue.EngineUnavailable);
        });
        var regions = new[] { Region("Hallo") };

        var first = await pipeline.TranslateAsync(regions, TranslationSelection.AutoToEnglish, CancellationToken.None);
        fakes.Catalog.AddRoute("de", "en", ModelVersion);
        var second = await pipeline.TranslateAsync(regions, TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.EngineUnavailable, first.Regions[0].Status);
        Assert.Equal(RegionTranslationStatus.Translated, second.Regions[0].Status);
    }

    [Fact]
    public async Task WorkerDownDuringDetection_MarksRegionsWorkerUnavailable()
    {
        var (pipeline, _) = Create(f => f.Detector.Failure = new WorkerException(WorkerErrorKind.NotConnected, "not connected"));

        var pass = await pipeline.TranslateAsync([Region("Hallo"), Region("Bonjour")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.All(pass.Regions, r =>
        {
            Assert.Equal(RegionTranslationStatus.WorkerUnavailable, r.Status);
            Assert.Null(r.TranslatedText);
        });
    }

    [Fact]
    public async Task WorkerDownDuringRouteQuery_MarksRegionsWorkerUnavailable()
    {
        var (pipeline, _) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Catalog.Failure = new WorkerException(WorkerErrorKind.ConnectionLost, "lost");
        });

        var pass = await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.WorkerUnavailable, Assert.Single(pass.Regions).Status);
    }

    [Fact]
    public async Task WorkerDownDuringTranslation_MarksRegionsWorkerUnavailable()
    {
        var (pipeline, _) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Engine.BatchFailure = new TranslationException(new TranslationError(TranslationErrorKind.WorkerUnavailable, "worker is gone"));
        });

        var pass = await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.WorkerUnavailable, item.Status);
        Assert.Equal("de", item.SourceLanguage);
    }

    [Fact]
    public async Task OneFailingItem_DoesNotFailTheOthers()
    {
        var (pipeline, _) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Detector.Set("Guten Tag", "de", 0.95);
            f.Engine.FailText("Hallo", new TranslationError(TranslationErrorKind.Failed, "engine failed"));
        });

        var pass = await pipeline.TranslateAsync([Region("Hallo"), Region("Guten Tag")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.Failed, pass.Regions[0].Status);
        Assert.Equal(RegionTranslationStatus.Translated, pass.Regions[1].Status);
    }

    [Fact]
    public async Task FailedTranslations_AreNotCached()
    {
        var (pipeline, fakes) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Engine.FailText("Hallo", new TranslationError(TranslationErrorKind.Failed, "engine failed"));
        });
        var regions = new[] { Region("Hallo") };
        await pipeline.TranslateAsync(regions, TranslationSelection.AutoToEnglish, CancellationToken.None);
        fakes.Engine.ClearFailures();

        var second = await pipeline.TranslateAsync(regions, TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.Translated, second.Regions[0].Status);
        Assert.False(second.Regions[0].FromCache);
    }

    [Fact]
    public async Task EngineReturningTheWrongNumberOfOutcomes_FailsTheRegionsInsteadOfGuessing()
    {
        var (pipeline, _) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Engine.DropOutcomes = true;
        });

        var pass = await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.Failed, item.Status);
        Assert.Null(item.TranslatedText);
    }

    [Fact]
    public async Task EmptyInput_ReturnsAnEmptyPassWithoutContactingAnything()
    {
        var (pipeline, fakes) = Create();

        var pass = await pipeline.TranslateAsync([], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Empty(pass.Regions);
        Assert.Equal(0, fakes.Detector.Calls);
        Assert.Equal(0, fakes.Engine.Calls);
    }

    [Fact]
    public async Task CancelledToken_StopsThePass_WithoutTranslating()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo", "de", 0.95));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, cts.Token));

        Assert.Equal(0, fakes.Engine.Calls);
    }

    [Fact]
    public async Task CancellingWhileTheEngineWorks_PropagatesCancellation()
    {
        var gate = new TaskCompletionSource();
        var (pipeline, fakes) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Engine.Hold = gate.Task;
        });
        using var cts = new CancellationTokenSource();

        var pass = pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, cts.Token);
        await fakes.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        gate.TrySetResult();
    }

    [Fact]
    public async Task StalePass_WaitingForItsTurn_IsCancelledWithoutRunning()
    {
        var gate = new TaskCompletionSource();
        var (pipeline, fakes) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Detector.Set("Guten Tag", "de", 0.95);
            f.Engine.Hold = gate.Task;
        });

        var first = pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);
        await fakes.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var staleCts = new CancellationTokenSource();
        var stale = pipeline.TranslateAsync([Region("Guten Tag")], TranslationSelection.AutoToEnglish, staleCts.Token);
        await staleCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);

        gate.SetResult();
        var finished = await first;

        Assert.Equal(RegionTranslationStatus.Translated, Assert.Single(finished.Regions).Status);
        Assert.Equal(1, fakes.Engine.Calls);
        Assert.Equal(1, fakes.Detector.Calls);
    }

    [Fact]
    public async Task Passes_NeverRunConcurrently()
    {
        var (pipeline, fakes) = Create(f =>
        {
            f.Detector.Set("Hallo", "de", 0.95);
            f.Detector.Set("Guten Tag", "de", 0.95);
            f.Engine.Delay = TimeSpan.FromMilliseconds(40);
        });

        await Task.WhenAll(
            pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None),
            pipeline.TranslateAsync([Region("Guten Tag")], TranslationSelection.AutoToEnglish, CancellationToken.None),
            pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None));

        Assert.Equal(1, fakes.Engine.MaxConcurrent);
    }

    [Fact]
    public async Task ClearCaches_ForgetsTranslationsAndDetections()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo", "de", 0.95));
        await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        pipeline.ClearCaches();
        var second = await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.False(Assert.Single(second.Regions).FromCache);
        Assert.Equal(2, fakes.Detector.TextsDetected);
        Assert.Equal(2, fakes.Engine.TextsTranslated);
    }

    [Fact]
    public async Task CatalogChange_ClearsTheTranslationCache()
    {
        var (pipeline, fakes) = Create(f => f.Detector.Set("Hallo", "de", 0.95));
        await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);
        Assert.Equal(1, fakes.Cache.Count);

        fakes.Catalog.RaiseChanged();

        Assert.Equal(0, fakes.Cache.Count);
    }

    [Fact]
    public async Task Detector_ReportingNoConfidence_IsTrustedWithoutInventingOne()
    {
        var (pipeline, _) = Create(f => f.Detector.Set("Hallo", "de", null));

        var pass = await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        var item = Assert.Single(pass.Regions);
        Assert.Equal(RegionTranslationStatus.Translated, item.Status);
        Assert.Null(item.DetectionConfidence);
    }

    [Fact]
    public async Task ThresholdIsConfigurable()
    {
        var (pipeline, _) = Create(f => f.Detector.Set("Hallo", "de", 0.7), new TranslationPipelineOptions { MinimumConfidence = 0.9 });

        var pass = await pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(RegionTranslationStatus.LanguageUncertain, Assert.Single(pass.Regions).Status);
    }

    [Fact]
    public async Task MixedRegions_GetIndependentStatuses()
    {
        var (pipeline, _) = Create(f =>
        {
            f.Detector.Set("Hallo Welt", "de", 0.99);
            f.Detector.Set("Hello there", "en", 0.99);
            f.Detector.Set("こんにちは", "ja", 0.99);
            f.Detector.SetUndetermined("Ok", LanguageDetectionIssue.TooShort);
        });

        var pass = await pipeline.TranslateAsync(
            [Region("Hallo Welt"), Region("Hello there"), Region("こんにちは"), Region("Ok"), Region("42")],
            TranslationSelection.AutoToEnglish,
            CancellationToken.None);

        Assert.Equal(
            [
                RegionTranslationStatus.Translated,
                RegionTranslationStatus.AlreadyTarget,
                RegionTranslationStatus.ModelMissing,
                RegionTranslationStatus.LanguageUncertain,
                RegionTranslationStatus.NoText,
            ],
            pass.Regions.Select(r => r.Status));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies_AndBadThreshold()
    {
        var fakes = new Fakes();

        Assert.Throws<ArgumentNullException>(() => new TranslationPipeline(null!, fakes.Catalog, fakes.Engine, fakes.Cache));
        Assert.Throws<ArgumentNullException>(() => new TranslationPipeline(fakes.Detector, null!, fakes.Engine, fakes.Cache));
        Assert.Throws<ArgumentNullException>(() => new TranslationPipeline(fakes.Detector, fakes.Catalog, null!, fakes.Cache));
        Assert.Throws<ArgumentNullException>(() => new TranslationPipeline(fakes.Detector, fakes.Catalog, fakes.Engine, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TranslationPipeline(fakes.Detector, fakes.Catalog, fakes.Engine, fakes.Cache, new TranslationPipelineOptions { MinimumConfidence = 1.5 }));
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromCatalog_AndRejectsFurtherUse()
    {
        var (pipeline, fakes) = Create();

        pipeline.Dispose();

        Assert.Equal(0, fakes.Catalog.SubscriberCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pipeline.TranslateAsync([Region("Hallo")], TranslationSelection.AutoToEnglish, CancellationToken.None));
    }

    [Fact]
    public void TranslationSelection_ValidatesLanguageCodes()
    {
        Assert.Throws<ArgumentException>(() => new TranslationSelection("not a code", "en"));
        Assert.Throws<ArgumentException>(() => new TranslationSelection(null, "EN!"));
        Assert.Null(TranslationSelection.AutoToEnglish.SourceLanguage);
        Assert.Equal("en", TranslationSelection.AutoToEnglish.TargetLanguage);
    }

    [Fact]
    public async Task WithAGrouper_LinesOfOneParagraphAreTranslatedAsOneText_AndKeepTheirIdentity()
    {
        var (pipeline, fakes) = Create(
            f => f.Detector.Set("这家餐厅的菜很好吃，就是价格有点贵。", "zh", null),
            new TranslationPipelineOptions { Grouper = new ParagraphRegionGrouper() });
        fakes.Catalog.AddRoute("zh", "en", "ct2/seg3|argos:zh-en:1.9:abc");
        var first = Region("这家餐厅的菜很好吃，", x: 34, y: 30, width: 220, height: 30);
        var second = Region("就是价格有点贵。", x: 34, y: 62, width: 180, height: 30);
        var other = Region("好的", x: 34, y: 150, width: 50, height: 30);
        fakes.Detector.Set("好的", "zh", null);

        var pass = await pipeline.TranslateAsync([first, second, other], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Equal(2, pass.Regions.Count); // two translation units, not three lines
        var paragraph = pass.Regions[0];
        Assert.Equal("[en] 这家餐厅的菜很好吃，就是价格有点贵。", paragraph.TranslatedText);
        Assert.Equal([first.Id, second.Id], paragraph.Members.Select(m => m.Id));
        Assert.Equal(first.Id, paragraph.RegionId);
        Assert.Equal((34, 30, 220, 62), (paragraph.Region.X, paragraph.Region.Y, paragraph.Region.Width, paragraph.Region.Height));
        Assert.Equal(0, paragraph.ReadingOrder);
        Assert.Equal([other.Id], pass.Regions[1].Members.Select(m => m.Id));
        Assert.Equal(1, pass.Regions[1].ReadingOrder);
        Assert.Equal(2, fakes.Engine.TextsTranslated); // the paragraph once, the short message once
    }

    [Fact]
    public async Task WithoutAGrouper_EveryResultStillListsItsOwnRegionAsTheOnlyMember()
    {
        var (pipeline, _) = Create(f => f.Detector.Set("Hallo", "de", 0.95));
        var region = Region("Hallo");

        var pass = await pipeline.TranslateAsync([region], TranslationSelection.AutoToEnglish, CancellationToken.None);

        Assert.Same(region, Assert.Single(Assert.Single(pass.Regions).Members));
        Assert.Equal(0, pass.Regions[0].ReadingOrder);
    }

    /// <summary>The scripted collaborators of the pipeline.</summary>
    private sealed class Fakes
    {
        public Fakes()
        {
            Catalog.AddRoute("de", "en", ModelVersion);
        }

        public FakeDetector Detector { get; } = new();

        public FakeCatalog Catalog { get; } = new();

        public FakeEngine Engine { get; } = new();

        public DictionaryCache Cache { get; } = new();
    }

    private sealed class FakeDetector : ILanguageDetector
    {
        private readonly Dictionary<string, LanguageDetectionResult> _results = new(StringComparer.Ordinal);
        private int _calls;
        private int _texts;

        public Exception? Failure { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public int TextsDetected => Volatile.Read(ref _texts);

        public void Set(string text, string language, double? confidence, params LanguageCandidate[] alternatives) =>
            _results[text] = LanguageDetectionResult.Detected(language, confidence, alternatives);

        public void SetUndetermined(string text, LanguageDetectionIssue issue) =>
            _results[text] = LanguageDetectionResult.Undetermined(issue);

        public Task<IReadOnlyList<LanguageDetectionResult>> DetectAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _texts, texts.Count);
            if (Failure is { } failure)
            {
                throw failure;
            }

            IReadOnlyList<LanguageDetectionResult> results = texts
                .Select(t => _results.TryGetValue(t, out var r) ? r : LanguageDetectionResult.Undetermined(LanguageDetectionIssue.Unknown))
                .ToList();
            return Task.FromResult(results);
        }
    }

    private sealed class FakeCatalog : IModelCatalog
    {
        private readonly Dictionary<(string, string), RouteStatus> _routes = [];
        private int _queries;

        public event EventHandler? Changed;

        public Exception? Failure { get; set; }

        public int RouteQueries => Volatile.Read(ref _queries);

        public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

        public void AddRoute(string source, string target, string modelVersion) =>
            _routes[(source, target)] = new RouteStatus(source, target, true, [source, target], modelVersion, null, RouteIssue.None);

        public void SetIssue(string source, string target, RouteIssue issue) =>
            _routes[(source, target)] = new RouteStatus(source, target, false, null, null, null, issue);

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<ModelCatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelCatalog("fake", null, [], []));

        public Task<RouteStatus> GetRouteStatusAsync(string source, string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _queries);
            if (Failure is { } failure)
            {
                throw failure;
            }

            var status = _routes.TryGetValue((source, target), out var known)
                ? known
                : new RouteStatus(source, target, false, null, null, $"translate-{source}_{target}", RouteIssue.ModelMissing);
            return Task.FromResult(status);
        }

        public Task<TranslationPackageInfo> ImportPackageAsync(string localFilePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEngine : ITranslationEngine
    {
        private readonly Dictionary<string, TranslationError> _failures = new(StringComparer.Ordinal);
        private int _calls;
        private int _texts;
        private int _active;
        private int _maxConcurrent;

        public Exception? BatchFailure { get; set; }

        public bool DropOutcomes { get; set; }

        public Task? Hold { get; set; }

        public TimeSpan Delay { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public int TextsTranslated => Volatile.Read(ref _texts);

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public void FailText(string text, TranslationError error) => _failures[text] = error;

        public void ClearFailures() => _failures.Clear();

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            var outcomes = await TranslateBatchAsync([request], cancellationToken);
            return outcomes[0].Result ?? throw new TranslationException(outcomes[0].Error!);
        }

        public async Task<IReadOnlyList<TranslationOutcome>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _texts, requests.Count);
            int active = Interlocked.Increment(ref _active);
            InterlockedMax(ref _maxConcurrent, active);
            Started.TrySetResult();
            try
            {
                if (Hold is { } hold)
                {
                    await hold.WaitAsync(cancellationToken);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                if (BatchFailure is { } failure)
                {
                    throw failure;
                }

                var outcomes = new List<TranslationOutcome>();
                foreach (var request in requests)
                {
                    outcomes.Add(
                        _failures.TryGetValue(request.Text, out var error)
                            ? TranslationOutcome.Failure(error)
                            : TranslationOutcome.Success(new TranslationResult(request.Text, $"[{request.TargetLanguage}] {request.Text}", request.SourceLanguage!, request.TargetLanguage, "fake-model")));
                }

                if (DropOutcomes)
                {
                    outcomes.Clear();
                }

                return outcomes;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref target);
                if (value <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }

    private sealed class DictionaryCache : ITranslationCache
    {
        private readonly Dictionary<TranslationCacheKey, TranslationResult> _items = [];
        private readonly Lock _lock = new();

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _items.Count;
                }
            }
        }

        public bool TryGet(TranslationCacheKey key, out TranslationResult? result)
        {
            lock (_lock)
            {
                return _items.TryGetValue(key, out result);
            }
        }

        public void Store(TranslationCacheKey key, TranslationResult result)
        {
            lock (_lock)
            {
                _items[key] = result;
            }
        }

        public void InvalidateModel(string modelVersion)
        {
            lock (_lock)
            {
                foreach (var key in _items.Keys.Where(k => k.ModelVersion == modelVersion).ToList())
                {
                    _items.Remove(key);
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _items.Clear();
            }
        }
    }
}
