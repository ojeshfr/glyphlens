using System.Diagnostics;
using GlyphLens.Core.Capture;
using GlyphLens.Core.Events;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Capture;
using GlyphLens.Windows.Display;

namespace GlyphLens.Windows.Tests.Integration;

/// <summary>
/// End-to-end tests against the real Windows Graphics Capture stack. Require an interactive desktop;
/// see <see cref="HardwareFactAttribute"/>.
/// </summary>
public sealed class CaptureIntegrationTests
{
    private const string TestWindowTitle = "GlyphLensCaptureTestWindow";
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    [HardwareFact]
    public void Provider_ListsDisplaysWithExactlyOnePrimary()
    {
        var targets = new CaptureTargetProvider().GetTargets();
        var displays = targets.Where(t => t.Kind == CaptureTargetKind.Display).ToList();

        Assert.NotEmpty(displays);
        Assert.Single(displays, d => d.IsPrimary);
        Assert.All(displays, d =>
        {
            Assert.True(d.Width > 0 && d.Height > 0);
            Assert.NotNull(d.Bounds);
            Assert.NotNull(d.Dpi);
        });
        Assert.DoesNotContain(targets.Where(t => t.Kind == CaptureTargetKind.Window), w => w.IsPrimary);
    }

    [HardwareFact]
    public async Task PrimaryDisplay_DeliversFramesWithPhysicalPixelSize_ThenStopsCleanly()
    {
        var display = new CaptureTargetProvider().GetTargets().First(t => t is { Kind: CaptureTargetKind.Display, IsPrimary: true });
        await using var service = new WindowsGraphicsCaptureService();
        var statuses = Record(service);

        await service.StartAsync(display);
        Assert.Equal(CaptureStatus.Capturing, service.Status);
        Assert.Equal(display, service.CurrentTarget);

        using var cts = new CancellationTokenSource(FrameTimeout);
        var latest = await service.Frames.WaitForNewerAsync(0, cts.Token);
        Assert.NotNull(latest);
        using (var frame = latest.Value.Frame)
        {
            Assert.Equal(display.Width, frame.Metadata.Width);
            Assert.Equal(display.Height, frame.Metadata.Height);
            Assert.Equal(display.Id, frame.Metadata.TargetId);
            Assert.Equal(PixelFormat.Bgra8, frame.Metadata.PixelFormat);
            Assert.True(frame.Metadata.Sequence >= 1);
            Assert.Equal(display.Bounds, frame.Metadata.ScreenBounds);
            Assert.Equal(frame.Metadata.Stride * frame.Metadata.Height, frame.Pixels.Length);
            Assert.Contains(frame.Pixels.Span.ToArray(), b => b != 0); // a real desktop is not all-black
        }

        await service.StopAsync();

        Assert.Equal(CaptureStatus.Idle, service.Status);
        Assert.Null(service.CurrentTarget);
        Assert.Null(service.Frames.LatestMetadata);
        Assert.Equal(
            [CaptureStatus.Starting, CaptureStatus.Capturing, CaptureStatus.Stopping, CaptureStatus.Idle],
            statuses.Select(s => s.CurrentStatus));
    }

    [HardwareFact]
    public async Task RepeatedStartStop_ReleasesNativeResources()
    {
        var display = new CaptureTargetProvider().GetTargets().First(t => t is { Kind: CaptureTargetKind.Display, IsPrimary: true });
        await using var service = new WindowsGraphicsCaptureService();

        await RunCycle(service, display); // warm-up: loads runtime and driver components once
        await Settle();
        var sectionsBefore = ProcessHandleCounter.Count("Section");

        const int Cycles = 30;
        for (var i = 0; i < Cycles; i++)
        {
            await RunCycle(service, display);
            Assert.Equal(CaptureStatus.Idle, service.Status);
        }

        await Settle();
        var sectionsAfter = ProcessHandleCounter.Count("Section");

        // Regression guard. Each frame-pool buffer holds a kernel Section handle until Direct3D destroys the texture.
        // Before the shared-device/flush fix this grew by two per cycle (about 60 over 30 cycles).
        Assert.True(
            sectionsAfter - sectionsBefore < 8,
            $"Section handle count grew from {sectionsBefore} to {sectionsAfter} across {Cycles} start/stop cycles.");
    }

    [HardwareFact]
    public async Task StartingWhileCapturing_IsRejected_AndCancelledStartReturnsToIdle()
    {
        var display = new CaptureTargetProvider().GetTargets().First(t => t is { Kind: CaptureTargetKind.Display, IsPrimary: true });
        await using var service = new WindowsGraphicsCaptureService();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(display, cancelled.Token));
        Assert.Equal(CaptureStatus.Idle, service.Status);

        await service.StartAsync(display);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(display));
        Assert.Equal(CaptureStatus.Capturing, service.Status);
    }

    [HardwareFact]
    public async Task UnknownWindow_FaultsWithTargetNotFound_AndServiceRecovers()
    {
        var display = new CaptureTargetProvider().GetTargets().First(t => t is { Kind: CaptureTargetKind.Display, IsPrimary: true });
        var stale = new CaptureTarget(CaptureTargetId.ForWindow(0x7FFFFFF0), CaptureTargetKind.Window, "Gone", 100, 100);
        await using var service = new WindowsGraphicsCaptureService();

        var ex = await Assert.ThrowsAsync<CaptureException>(() => service.StartAsync(stale));

        Assert.Equal(CaptureErrorKind.TargetNotFound, ex.Error.Kind);
        Assert.Equal(CaptureStatus.Faulted, service.Status);

        await service.StartAsync(display); // Faulted -> Starting is allowed
        Assert.Equal(CaptureStatus.Capturing, service.Status);
    }

    [HardwareFact]
    public async Task Window_IsCaptured_AndClosingItFaultsWithTargetClosed()
    {
        using var testWindow = StartTestWindowProcess();
        var provider = new CaptureTargetProvider();
        var target = await WaitForWindow(provider, TestWindowTitle);

        await using var service = new WindowsGraphicsCaptureService();
        var statuses = Record(service);
        await service.StartAsync(target);

        using var cts = new CancellationTokenSource(FrameTimeout);
        var latest = await service.Frames.WaitForNewerAsync(0, cts.Token);
        Assert.NotNull(latest);
        using (var frame = latest.Value.Frame)
        {
            Assert.Equal(target.Id, frame.Metadata.TargetId);
            Assert.True(frame.Metadata.Width > 0 && frame.Metadata.Height > 0);
            Assert.Null(frame.Metadata.ScreenBounds);
        }

        testWindow.Kill(entireProcessTree: true);
        await testWindow.WaitForExitAsync();

        var faulted = await WaitUntil(() => service.Status == CaptureStatus.Faulted, TimeSpan.FromSeconds(10));
        Assert.True(faulted, "Closing the captured window should fault the capture.");
        var fault = statuses.Last(s => s.CurrentStatus == CaptureStatus.Faulted);
        Assert.Equal(CaptureErrorKind.TargetClosed, fault.Error?.Kind);
        Assert.Null(service.CurrentTarget);

        await service.StopAsync(); // acknowledges the fault
        Assert.Equal(CaptureStatus.Idle, service.Status);
    }

    [HardwareFact]
    public async Task DisposeWhileCapturing_StopsCaptureAndDoesNotRaiseLaterEvents()
    {
        var display = new CaptureTargetProvider().GetTargets().First(t => t is { Kind: CaptureTargetKind.Display, IsPrimary: true });
        var service = new WindowsGraphicsCaptureService();
        var statuses = Record(service);
        await service.StartAsync(display);

        await service.DisposeAsync();
        var countAfterDispose = statuses.Count;
        await Task.Delay(500);

        Assert.Equal(countAfterDispose, statuses.Count);
        Assert.Equal(CaptureStatus.Idle, service.Status);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(display));
    }

    private static async Task RunCycle(WindowsGraphicsCaptureService service, CaptureTarget target)
    {
        await service.StartAsync(target);
        using var cts = new CancellationTokenSource(FrameTimeout);
        var latest = await service.Frames.WaitForNewerAsync(service.Frames.Version, cts.Token);
        latest?.Frame.Dispose();
        await service.StopAsync();
    }

    private static List<CaptureStatusChangedEventArgs> Record(WindowsGraphicsCaptureService service)
    {
        var list = new List<CaptureStatusChangedEventArgs>();
        service.StatusChanged += (_, args) =>
        {
            lock (list)
            {
                list.Add(args);
            }
        };
        return list;
    }

    /// <summary>Lets the service's delayed post-stop release (wait, finalize, flush) finish.</summary>
    private static async Task Settle()
    {
        await Task.Delay(1500);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static async Task<CaptureTarget> WaitForWindow(CaptureTargetProvider provider, string title)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var match = provider.GetTargets().FirstOrDefault(t => t.Kind == CaptureTargetKind.Window && t.DisplayName == title);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"The test window '{title}' never appeared in the window list.");
    }

    /// <summary>Starts a separate process that shows a plain window with a known title.</summary>
    private static Process StartTestWindowProcess()
    {
        var script =
            "Add-Type -AssemblyName System.Windows.Forms; "
            + $"$f = New-Object System.Windows.Forms.Form; $f.Text = '{TestWindowTitle}'; $f.Width = 640; $f.Height = 400; "
            + "[System.Windows.Forms.Application]::Run($f)";
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start the test window process.");
    }
}
