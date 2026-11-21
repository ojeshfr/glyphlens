using GlyphLens.App.ViewModels;
using GlyphLens.Core.Models;

namespace GlyphLens.App.Tests;

public sealed class OverlayStatusEvaluatorTests
{
    private static PassSummary Pass(int regions = 3, int translated = 3, int modelMissing = 0, int uncertain = 0, int failed = 0, int worker = 0, int engine = 0, int noText = 0) =>
        new(regions, translated, modelMissing, uncertain, failed, worker, engine, noText);

    private static OverlayStatus Evaluate(
        bool enabled = true,
        bool paused = false,
        WorkerStatus worker = WorkerStatus.Ready,
        bool? capturing = true,
        bool live = false,
        PassSummary? pass = null) =>
        OverlayStatusEvaluator.Evaluate(new OverlayStatusInput(enabled, paused, worker, capturing, live, pass));

    [Fact]
    public void TranslationOff_IsOff_AndNeutral()
    {
        var status = Evaluate(enabled: false);

        Assert.Equal(OverlayStateKind.Off, status.Kind);
        Assert.Equal(StatusLevel.Neutral, status.Level);
    }

    [Fact]
    public void Paused_WinsOverEverythingExceptOff()
    {
        Assert.Equal(OverlayStateKind.Paused, Evaluate(paused: true, worker: WorkerStatus.Error).Kind);
        Assert.Equal(OverlayStateKind.Off, Evaluate(enabled: false, paused: true).Kind);
    }

    [Fact]
    public void AStartingWorker_IsStarting_NotGreen()
    {
        var status = Evaluate(worker: WorkerStatus.Starting);

        Assert.Equal(OverlayStateKind.Starting, status.Kind);
        Assert.NotEqual(StatusLevel.Ok, status.Level);
    }

    [Theory]
    [InlineData(WorkerStatus.Error)]
    [InlineData(WorkerStatus.Unavailable)]
    [InlineData(WorkerStatus.Stopped)]
    public void AWorkerThatIsNotRunning_IsAProblem_WhileTranslationIsOn(WorkerStatus worker)
    {
        var status = Evaluate(worker: worker);

        Assert.Equal(OverlayStateKind.WorkerUnavailable, status.Kind);
        Assert.Equal(StatusLevel.Problem, status.Level);
    }

    [Fact]
    public void APassThatCouldNotReachTheWorkerOrEngine_IsAProblemEvenIfTheProcessIsReady()
    {
        Assert.Equal(OverlayStateKind.WorkerUnavailable, Evaluate(pass: Pass(translated: 0, worker: 3)).Kind);
        Assert.Equal(OverlayStateKind.WorkerUnavailable, Evaluate(pass: Pass(translated: 0, engine: 1)).Kind);
    }

    [Fact]
    public void NoCapture_IsCaptureStopped_AndNeverLiveOrReady()
    {
        var status = Evaluate(capturing: false, live: true);

        Assert.Equal(OverlayStateKind.CaptureStopped, status.Kind);
        Assert.Equal(StatusLevel.Attention, status.Level);
    }

    [Fact]
    public void AnUnknownCaptureState_DoesNotBlockReady() =>
        Assert.Equal(OverlayStateKind.Ready, Evaluate(capturing: null).Kind);

    [Fact]
    public void AMissingModel_IsAttention_AndNamedAsSuch()
    {
        var status = Evaluate(pass: Pass(translated: 0, modelMissing: 3));

        Assert.Equal(OverlayStateKind.ModelMissing, status.Kind);
        Assert.Equal("Model missing", status.Text);
        Assert.Equal(StatusLevel.Attention, status.Level);
    }

    [Fact]
    public void FailedLines_AreAnError() =>
        Assert.Equal(OverlayStateKind.Error, Evaluate(pass: Pass(translated: 2, failed: 1)).Kind);

    [Fact]
    public void UncertainLanguage_WithNothingTranslated_IsUncertain_ButNotWhenSomethingWas()
    {
        Assert.Equal(OverlayStateKind.Uncertain, Evaluate(pass: Pass(translated: 0, uncertain: 3)).Kind);
        Assert.Equal(OverlayStateKind.Ready, Evaluate(pass: Pass(translated: 2, uncertain: 1)).Kind);
    }

    [Fact]
    public void AReadScreenWithoutText_IsNoTextFound_NotAnError()
    {
        Assert.Equal(OverlayStateKind.NoText, Evaluate(pass: Pass(regions: 0, translated: 0)).Kind);
        Assert.Equal(OverlayStateKind.NoText, Evaluate(pass: Pass(regions: 2, translated: 0, noText: 2)).Kind);
    }

    [Fact]
    public void Ready_IsGreen_OnlyWhenEverythingNeededIsRunningAndNothingIsWrong()
    {
        var ready = Evaluate(pass: Pass());
        var live = Evaluate(live: true, pass: Pass());

        Assert.Equal((OverlayStateKind.Ready, StatusLevel.Ok), (ready.Kind, ready.Level));
        Assert.Equal((OverlayStateKind.Live, StatusLevel.Ok), (live.Kind, live.Level));
    }

    [Fact]
    public void BeforeTheFirstPass_WithEverythingRunning_IsReady() =>
        Assert.Equal(OverlayStateKind.Ready, Evaluate(pass: null).Kind);

    [Fact]
    public void ThePassSummary_CountsEachOutcome()
    {
        var region = new Core.Models.ScreenTextRegion(Guid.NewGuid(), "x", 0, 0, 1, 1, Core.Models.DetectionSource.Ocr, null, null, DateTimeOffset.UnixEpoch);
        var regions = new[]
        {
            new Core.Translation.RegionTranslation { Region = region, Status = Core.Translation.RegionTranslationStatus.Translated },
            new Core.Translation.RegionTranslation { Region = region, Status = Core.Translation.RegionTranslationStatus.ModelMissing },
            new Core.Translation.RegionTranslation { Region = region, Status = Core.Translation.RegionTranslationStatus.NoText },
        };

        var summary = PassSummary.From(regions);

        Assert.Equal(new PassSummary(3, 1, 1, 0, 0, 0, 0, 1), summary);
    }
}
