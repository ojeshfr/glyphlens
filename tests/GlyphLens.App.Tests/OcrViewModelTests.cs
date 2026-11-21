using GlyphLens.App.ViewModels;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Core.Ocr;

namespace GlyphLens.App.Tests;

public class OcrViewModelTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
    private static readonly CaptureTarget Display = new("display:1", CaptureTargetKind.Display, "Display 1", 1920, 1080, true);

    private sealed record Rig(OcrViewModel Vm, FakeOcr Ocr, FakeCaptureService Capture, OverlayState Overlay, List<string> Copied);

    private static Rig Create(TimeSpan? liveInterval = null, Action<string>? copy = null)
    {
        var ocr = new FakeOcr();
        var capture = new FakeCaptureService();
        var overlay = new OverlayState();
        var copied = new List<string>();
        var vm = new OcrViewModel(ocr, ocr, capture, overlay, copy ?? copied.Add, uiContext: null, liveInterval ?? TimeSpan.FromMilliseconds(10));
        return new Rig(vm, ocr, capture, overlay, copied);
    }

    private static CapturedFrame Frame(long sequence, int width = 1920, int height = 1080)
    {
        var metadata = new FrameMetadata("display:1", width, height, width * 4, PixelFormat.Bgra8, Stamp, sequence);
        return new CapturedFrame(metadata, new byte[metadata.ByteLength]);
    }

    private static async Task StartCaptureWithFrameAsync(Rig rig, int width = 1920, int height = 1080)
    {
        await rig.Capture.StartAsync(Display);
        rig.Capture.Mailbox.Publish(Frame(1, width, height));
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
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

    [Fact]
    public void InitialState_OffersTheInstalledLanguagesAndAnAutomaticChoice()
    {
        var rig = Create();

        Assert.Equal(["Automatic (your Windows languages)", "English (United States)", "German"], rig.Vm.Languages.Select(l => l.Label));
        Assert.Null(rig.Vm.SelectedLanguage.Tag);
        Assert.False(rig.Vm.CanReadText);
        Assert.False(rig.Vm.CanCopy);
        Assert.False(rig.Vm.HasError);
        Assert.Equal("Start a capture, then read its text.", rig.Vm.StatusText);
    }

    [Fact]
    public void NoInstalledLanguage_IsExplainedUpFront()
    {
        var ocr = new FakeOcr { AvailableLanguages = [] };

        var vm = new OcrViewModel(ocr, ocr, new FakeCaptureService(), new OverlayState(), null, null, TimeSpan.FromMilliseconds(10));

        Assert.True(vm.HasError);
        Assert.Contains("Windows Settings", vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadText_IsOnlyOfferedWhileCapturing()
    {
        var rig = Create();
        Assert.False(rig.Vm.ReadTextCommand.CanExecute(null));

        await StartCaptureWithFrameAsync(rig);

        Assert.True(rig.Vm.CanReadText);
        Assert.True(rig.Vm.ReadTextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReadText_ShowsTheLines_TheOverlayBoxes_AndASummary()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig, 2560, 1600);

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.Equal(1, rig.Ocr.Calls);
        Assert.Equal(2560, rig.Ocr.LastFrameWidth);
        Assert.Equal(["Hello world", "Second line"], rig.Vm.Lines.Select(l => l.Text));
        Assert.Equal("10,20  200×30", rig.Vm.Lines[0].BoundsText);
        Assert.Equal(2, rig.Overlay.Boxes.Count);
        Assert.Equal(new OcrBox(10, 20, 200, 30, "Hello world"), rig.Overlay.Boxes[0]);
        Assert.Equal(2560, rig.Overlay.FrameWidth);
        Assert.Equal(1600, rig.Overlay.FrameHeight);
        Assert.StartsWith("2 lines · ", rig.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("en-US", rig.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(rig.Vm.HasError);
        Assert.True(rig.Vm.CanCopy);
        Assert.Equal($"Hello world{Environment.NewLine}Second line", rig.Vm.RecognisedText);
    }

    [Fact]
    public async Task ReadText_OfAWindow_SetsAsideItsTitleBarGlyphAndTitle_AndSaysSo()
    {
        var rig = Create();
        rig.Ocr.Result =
        [
            FakeOcr.Region("- Notes", 12, 8, 90, 24),
            FakeOcr.Region("X", 1880, 8, 22, 25),
            FakeOcr.Region("Hello world", 10, 120, 200, 30),
        ];
        await rig.Capture.StartAsync(new CaptureTarget("window:1", CaptureTargetKind.Window, "Notes", 1920, 1080));
        rig.Capture.Mailbox.Publish(Frame(1));

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.Equal(["Hello world"], rig.Vm.Lines.Select(l => l.Text));
        Assert.Single(rig.Overlay.Boxes);
        Assert.Contains("2 title-bar items set aside", rig.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadText_OfADisplay_KeepsATitleLikeLineButStillDropsAButtonGlyph()
    {
        var rig = Create();
        rig.Ocr.Result =
        [
            FakeOcr.Region("Notes", 12, 8, 90, 24),
            FakeOcr.Region("X", 1880, 8, 22, 25),
        ];
        await StartCaptureWithFrameAsync(rig);

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.Equal(["Notes"], rig.Vm.Lines.Select(l => l.Text));
        Assert.Contains("1 title-bar item set aside", rig.Vm.StatusText, StringComparison.Ordinal);
    }

    private sealed class ReportingOcr(FakeOcr inner) : IOcrEngine, IOcrPassReporter
    {
        public OcrPassInfo? LastPass { get; set; }

        public Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) =>
            inner.RecognizeAsync(frame, cancellationToken);
    }

    [Theory]
    [InlineData(OcrPassKind.Partial, 0.123, " · updated 12 % of the frame")]
    [InlineData(OcrPassKind.Unchanged, 0.0, " · screen unchanged")]
    [InlineData(OcrPassKind.Full, 1.0, "")]
    public async Task ReadText_SaysWhenOnlyPartOfTheFrameWasRead(OcrPassKind kind, double fraction, string expectedSuffix)
    {
        var fake = new FakeOcr();
        var engine = new ReportingOcr(fake) { LastPass = new OcrPassInfo(kind, fraction, 1, null) };
        var capture = new FakeCaptureService();
        var vm = new OcrViewModel(engine, fake, capture, new OverlayState(), null, null, TimeSpan.FromMilliseconds(10));
        await capture.StartAsync(Display);
        capture.Mailbox.Publish(Frame(1));

        await vm.ReadTextCommand.ExecuteAsync();

        Assert.EndsWith("en-US" + expectedSuffix, vm.StatusText, StringComparison.Ordinal); // the recognised language comes first
    }

    [Fact]
    public async Task ReadText_ReleasesTheFrameItBorrowed()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        var before = rig.Capture.Mailbox.TryAcquireLatest()!.Value.Frame;
        var referencesBefore = before.ReferenceCount;
        before.Dispose();

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        var after = rig.Capture.Mailbox.TryAcquireLatest()!.Value.Frame;
        Assert.Equal(referencesBefore, after.ReferenceCount); // the view model returned its reference
        after.Dispose();
    }

    [Fact]
    public async Task NoText_IsReportedAsZeroLines_NotAnError()
    {
        var rig = Create();
        rig.Ocr.Result = [];
        await StartCaptureWithFrameAsync(rig);

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.Empty(rig.Vm.Lines);
        Assert.Empty(rig.Overlay.Boxes);
        Assert.StartsWith("0 lines · ", rig.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(rig.Vm.HasError);
        Assert.False(rig.Vm.CanCopy);
    }

    [Fact]
    public async Task AnEngineError_IsShown_AndPreviousResultsAreKept()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        await rig.Vm.ReadTextCommand.ExecuteAsync();

        rig.Ocr.Failure = new OcrException(OcrErrorKind.EngineUnavailable, "Windows OCR is not available on this system.");
        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.Equal("Windows OCR is not available on this system.", rig.Vm.ErrorMessage);
        Assert.Equal(2, rig.Vm.Lines.Count);
    }

    [Fact]
    public async Task AnUnexpectedException_NeverLeaksItsMessage()
    {
        var rig = Create();
        rig.Ocr.Failure = new InvalidOperationException("contained TOP-SECRET-SCREEN-TEXT");
        await StartCaptureWithFrameAsync(rig);

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.True(rig.Vm.HasError);
        Assert.Contains("InvalidOperationException", rig.Vm.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", rig.Vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulRead_ClearsAnEarlierError()
    {
        var rig = Create();
        rig.Ocr.Failure = new OcrException("first failure");
        await StartCaptureWithFrameAsync(rig);
        await rig.Vm.ReadTextCommand.ExecuteAsync();
        Assert.True(rig.Vm.HasError);

        rig.Ocr.Failure = null;
        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.False(rig.Vm.HasError);
    }

    [Fact]
    public async Task WhileReading_ReadingAgainIsNotOffered()
    {
        var rig = Create();
        var gate = new TaskCompletionSource();
        rig.Ocr.Gate = gate.Task;
        await StartCaptureWithFrameAsync(rig);

        var reading = rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.True(rig.Vm.IsReading);
        Assert.False(rig.Vm.CanReadText);
        gate.SetResult();
        await reading;
        Assert.False(rig.Vm.IsReading);
        Assert.Equal(1, rig.Ocr.Calls);
    }

    [Fact]
    public async Task StoppingTheCapture_ClearsTheResults_TheOverlayAndTheStatus()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        await rig.Vm.ReadTextCommand.ExecuteAsync();

        await rig.Capture.StopAsync();

        Assert.Empty(rig.Vm.Lines);
        Assert.Empty(rig.Overlay.Boxes);
        Assert.Equal(0, rig.Overlay.FrameWidth);
        Assert.Equal(string.Empty, rig.Vm.RecognisedText);
        Assert.False(rig.Vm.CanReadText);
        Assert.False(rig.Vm.CanCopy);
        Assert.Equal("Start a capture, then read its text.", rig.Vm.StatusText);
    }

    [Fact]
    public async Task EveryReadingPass_RaisesRegionsRecognised_WithTheRegionsOfThatPass()
    {
        var rig = Create();
        var raised = new List<RegionsRecognisedEventArgs>();
        rig.Vm.RegionsRecognised += (_, e) => raised.Add(e);
        await StartCaptureWithFrameAsync(rig);

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        var args = Assert.Single(raised);
        Assert.False(args.Discarded);
        Assert.Equal(rig.Ocr.Result.Select(r => r.Id), args.Regions.Select(r => r.Id));
        Assert.Equal(rig.Ocr.Result.Select(r => (r.X, r.Y, r.Width, r.Height)), args.Regions.Select(r => (r.X, r.Y, r.Width, r.Height)));
    }

    [Fact]
    public async Task StoppingTheCapture_RaisesADiscardedEvent_SoDerivedResultsAreForgotten()
    {
        var rig = Create();
        var raised = new List<RegionsRecognisedEventArgs>();
        await StartCaptureWithFrameAsync(rig);
        await rig.Vm.ReadTextCommand.ExecuteAsync();
        rig.Vm.RegionsRecognised += (_, e) => raised.Add(e);

        await rig.Capture.StopAsync();

        var args = Assert.Single(raised);
        Assert.True(args.Discarded);
        Assert.Empty(args.Regions);
    }

    [Fact]
    public async Task AFailedReading_DoesNotRaiseRegionsRecognised()
    {
        var rig = Create();
        var raised = 0;
        rig.Vm.RegionsRecognised += (_, _) => raised++;
        await StartCaptureWithFrameAsync(rig);
        rig.Ocr.Failure = new OcrException("engine failed");

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ACaptureThatFaults_AlsoClearsTheResults()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        await rig.Vm.ReadTextCommand.ExecuteAsync();

        rig.Capture.Fault(new CaptureError(CaptureErrorKind.TargetClosed, "closed"));

        Assert.Empty(rig.Vm.Lines);
        Assert.Empty(rig.Overlay.Boxes);
    }

    [Fact]
    public async Task ReadText_WithNoFrameYet_SaysSo()
    {
        var rig = Create();
        await rig.Capture.StartAsync(Display); // capturing, but no frame has arrived

        await rig.Vm.ReadTextCommand.ExecuteAsync();

        Assert.Equal("No frame has been captured yet.", rig.Vm.ErrorMessage);
        Assert.Equal(0, rig.Ocr.Calls);
    }

    // ---- language ----

    [Fact]
    public void SelectingALanguage_TellsTheEngine()
    {
        var rig = Create();

        rig.Vm.SelectedLanguage = rig.Vm.Languages.Single(l => l.Tag == "de-DE");
        Assert.Equal("de-DE", rig.Ocr.SelectedLanguage);

        rig.Vm.SelectedLanguage = rig.Vm.Languages[0];
        Assert.Null(rig.Ocr.SelectedLanguage);
    }

    [Fact]
    public void AnAlreadySelectedEngineLanguage_IsReflectedInTheSelector()
    {
        var ocr = new FakeOcr { SelectedLanguage = "de-DE" };

        var vm = new OcrViewModel(ocr, ocr, new FakeCaptureService(), new OverlayState(), null, null, TimeSpan.FromMilliseconds(10));

        Assert.Equal("de-DE", vm.SelectedLanguage.Tag);
    }

    // ---- copy ----

    [Fact]
    public async Task Copy_SendsTheRecognisedTextToTheClipboardHandler_OnlyOnCommand()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        await rig.Vm.ReadTextCommand.ExecuteAsync();
        Assert.Empty(rig.Copied); // reading alone never touches the clipboard

        await rig.Vm.CopyTextCommand.ExecuteAsync();

        Assert.Equal([$"Hello world{Environment.NewLine}Second line"], rig.Copied);
        Assert.Equal("Copied the recognised text.", rig.Vm.StatusText);
    }

    [Fact]
    public async Task Copy_IsNotOfferedWithoutText()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);

        Assert.False(rig.Vm.CopyTextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ABusyClipboard_BecomesAMessage_NotACrash()
    {
        var rig = Create(copy: _ => throw new System.Runtime.InteropServices.COMException("CLIPBRD_E_CANT_OPEN"));
        await StartCaptureWithFrameAsync(rig);
        await rig.Vm.ReadTextCommand.ExecuteAsync();

        await rig.Vm.CopyTextCommand.ExecuteAsync();

        Assert.Equal("The clipboard is busy; try again.", rig.Vm.ErrorMessage);
    }

    // ---- live ----

    [Fact]
    public async Task Live_CannotBeStartedWithoutACapture()
    {
        var rig = Create();

        rig.Vm.IsLive = true;

        Assert.False(rig.Vm.IsLive);
        Assert.Contains("Start a capture", rig.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, rig.Ocr.Calls);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Live_ReadsEachNewFrame_UntilStopped()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        rig.Vm.IsLive = true;
        Assert.True(rig.Vm.IsLive);
        Assert.False(rig.Vm.CanReadText); // manual reading is off while live

        for (var i = 2; i <= 6; i++)
        {
            Assert.True(await UntilAsync(() => rig.Ocr.Calls >= i - 1), $"live reading did not reach read #{i - 1}");
            rig.Capture.Mailbox.Publish(Frame(i));
        }

        Assert.True(await UntilAsync(() => rig.Ocr.Calls >= 4));
        rig.Vm.IsLive = false;
        await rig.Vm.WaitForLiveStoppedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var callsWhenStopped = rig.Ocr.Calls;
        rig.Capture.Mailbox.Publish(Frame(99));
        await Task.Delay(200);

        Assert.Equal(callsWhenStopped, rig.Ocr.Calls); // nothing reads after Stop
        Assert.False(rig.Vm.IsLive);
        Assert.Equal(2, rig.Vm.Lines.Count);
    }

    [Fact]
    public async Task Live_NeverQueuesWork_WhenFramesArriveFasterThanTheEngine()
    {
        var rig = Create(liveInterval: TimeSpan.FromMilliseconds(5));
        var gate = new TaskCompletionSource();
        rig.Ocr.Gate = gate.Task;
        await StartCaptureWithFrameAsync(rig);

        rig.Vm.IsLive = true;
        Assert.True(await UntilAsync(() => rig.Ocr.Calls == 1)); // the first read is now blocked inside the engine
        for (var i = 2; i <= 500; i++)
        {
            rig.Capture.Mailbox.Publish(Frame(i)); // a burst of 499 frames while the engine is busy
        }

        await Task.Delay(150);
        Assert.Equal(1, rig.Ocr.Calls); // still one in flight, none queued behind it
        gate.SetResult();
        Assert.True(await UntilAsync(() => rig.Ocr.Calls == 2));
        rig.Vm.IsLive = false;
        await rig.Vm.WaitForLiveStoppedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rig.Ocr.Calls <= 4, $"expected the burst to collapse into the newest frame, but the engine ran {rig.Ocr.Calls} times");
    }

    [Fact]
    public async Task Live_StopsWhenTheCaptureStops_AndResetsTheToggle()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        rig.Vm.IsLive = true;
        Assert.True(await UntilAsync(() => rig.Ocr.Calls >= 1));

        await rig.Capture.StopAsync();
        await rig.Vm.WaitForLiveStoppedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(rig.Vm.IsLive);
        Assert.Empty(rig.Vm.Lines);
    }

    [Fact]
    public async Task Live_StopsOnAnEngineError_InsteadOfRepeatingIt()
    {
        var rig = Create();
        rig.Ocr.Failure = new OcrException(OcrErrorKind.EngineUnavailable, "Windows OCR is not available on this system.");
        await StartCaptureWithFrameAsync(rig);

        rig.Vm.IsLive = true;
        await rig.Vm.WaitForLiveStoppedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(rig.Vm.IsLive);
        Assert.Equal("Windows OCR is not available on this system.", rig.Vm.ErrorMessage);
        Assert.Equal(1, rig.Ocr.Calls);
    }

    [Fact]
    public async Task Dispose_StopsLiveReading_AndUnsubscribes()
    {
        var rig = Create();
        await StartCaptureWithFrameAsync(rig);
        rig.Vm.IsLive = true;
        Assert.True(await UntilAsync(() => rig.Ocr.Calls >= 1));

        rig.Vm.Dispose();
        await rig.Vm.WaitForLiveStoppedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var calls = rig.Ocr.Calls;
        rig.Capture.Mailbox.Publish(Frame(50));
        await Task.Delay(150);

        Assert.Equal(calls, rig.Ocr.Calls);
        rig.Vm.Dispose(); // idempotent
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var ocr = new FakeOcr();
        var capture = new FakeCaptureService();

        Assert.Throws<ArgumentNullException>(() => new OcrViewModel(null!, ocr, capture, new OverlayState()));
        Assert.Throws<ArgumentNullException>(() => new OcrViewModel(ocr, null!, capture, new OverlayState()));
        Assert.Throws<ArgumentNullException>(() => new OcrViewModel(ocr, ocr, null!, new OverlayState()));
        Assert.Throws<ArgumentNullException>(() => new OcrViewModel(ocr, ocr, capture, null!));
    }
}

public class OverlayStateTests
{
    [Fact]
    public void Set_PublishesBoxesAndTheFrameSize()
    {
        var overlay = new OverlayState();
        var changed = new List<string?>();
        overlay.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        overlay.Set([new OcrBox(1, 2, 3, 4, "x")], 1920, 1080);

        Assert.Single(overlay.Boxes);
        Assert.Equal(1920, overlay.FrameWidth);
        Assert.Equal(1080, overlay.FrameHeight);
        Assert.Contains(nameof(OverlayState.Boxes), changed);
        Assert.Contains(nameof(OverlayState.FrameWidth), changed);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var overlay = new OverlayState();
        overlay.Set([new OcrBox(1, 2, 3, 4, "x")], 100, 50);

        overlay.Clear();

        Assert.Empty(overlay.Boxes);
        Assert.Equal(0, overlay.FrameWidth);
        Assert.Equal(0, overlay.FrameHeight);
    }

    [Fact]
    public void Set_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new OverlayState().Set(null!, 1, 1));
    }
}
