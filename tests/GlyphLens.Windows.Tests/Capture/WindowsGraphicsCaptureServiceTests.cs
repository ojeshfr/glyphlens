using GlyphLens.Core.Capture;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Capture;

namespace GlyphLens.Windows.Tests.Capture;

/// <summary>
/// Behaviour of <see cref="WindowsGraphicsCaptureService"/> that needs no display, window or GPU. Real capture is
/// covered by the opt-in tests in the Integration folder.
/// </summary>
public class WindowsGraphicsCaptureServiceTests
{
    private static readonly CaptureTarget BogusWindow = new("not-a-valid-id", CaptureTargetKind.Window, "bogus", 100, 100);

    [Fact]
    public async Task NewService_IsIdleWithNoTargetOrFrame()
    {
        await using var service = new WindowsGraphicsCaptureService();

        Assert.Equal(CaptureStatus.Idle, service.Status);
        Assert.Null(service.CurrentTarget);
        Assert.Null(service.Frames.LatestMetadata);
        Assert.Equal(0, service.Frames.Version);
    }

    [Fact]
    public async Task StopWhenIdle_IsANoOp()
    {
        await using var service = new WindowsGraphicsCaptureService();
        var changes = 0;
        service.StatusChanged += (_, _) => changes++;

        await service.StopAsync();

        Assert.Equal(CaptureStatus.Idle, service.Status);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Start_WithAnUnrecognisedTarget_FaultsAndThrowsACaptureException()
    {
        await using var service = new WindowsGraphicsCaptureService();
        var statuses = new List<CaptureStatus>();
        service.StatusChanged += (_, args) => statuses.Add(args.CurrentStatus);

        var ex = await Assert.ThrowsAsync<CaptureException>(() => service.StartAsync(BogusWindow));

        // TargetNotFound normally; Unsupported where Windows Graphics Capture itself is unavailable.
        Assert.Contains(ex.Error.Kind, new[] { CaptureErrorKind.TargetNotFound, CaptureErrorKind.Unsupported });
        Assert.Equal(CaptureStatus.Faulted, service.Status);
        Assert.Null(service.CurrentTarget);
        Assert.Equal([CaptureStatus.Starting, CaptureStatus.Faulted], statuses);
    }

    [Fact]
    public async Task FaultedService_ReportsTheErrorOnTheStatusEvent_AndStopAcknowledgesIt()
    {
        await using var service = new WindowsGraphicsCaptureService();
        CaptureError? reported = null;
        service.StatusChanged += (_, args) => reported ??= args.Error;

        await Assert.ThrowsAsync<CaptureException>(() => service.StartAsync(BogusWindow));
        Assert.NotNull(reported);

        await service.StopAsync();
        Assert.Equal(CaptureStatus.Idle, service.Status);
    }

    [Fact]
    public async Task FaultedService_CanBeStartedAgain()
    {
        await using var service = new WindowsGraphicsCaptureService();

        await Assert.ThrowsAsync<CaptureException>(() => service.StartAsync(BogusWindow));
        await Assert.ThrowsAsync<CaptureException>(() => service.StartAsync(BogusWindow));

        Assert.Equal(CaptureStatus.Faulted, service.Status);
    }

    [Fact]
    public async Task Start_WithAnAlreadyCancelledToken_ThrowsWithoutChangingState()
    {
        await using var service = new WindowsGraphicsCaptureService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(BogusWindow, cts.Token));

        Assert.Equal(CaptureStatus.Idle, service.Status);
    }

    [Fact]
    public async Task Start_RejectsNullTarget()
    {
        await using var service = new WindowsGraphicsCaptureService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.StartAsync(null!));
    }

    [Fact]
    public async Task Start_AfterDispose_ThrowsObjectDisposed()
    {
        var service = new WindowsGraphicsCaptureService();
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync(BogusWindow));
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var service = new WindowsGraphicsCaptureService();

        await service.DisposeAsync();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ASubscriberThatThrows_DoesNotBreakTheServiceOrOtherSubscribers()
    {
        await using var service = new WindowsGraphicsCaptureService();
        var secondSubscriberCalls = 0;
        service.StatusChanged += (_, _) => throw new InvalidOperationException("bad subscriber");
        service.StatusChanged += (_, _) => secondSubscriberCalls++;

        await Assert.ThrowsAsync<CaptureException>(() => service.StartAsync(BogusWindow));

        Assert.Equal(2, secondSubscriberCalls); // Starting and Faulted
        Assert.Equal(CaptureStatus.Faulted, service.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(241)]
    public void Options_RejectUnreasonableFrameRates(int fps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsGraphicsCaptureService(new WindowsCaptureOptions { MaxFramesPerSecond = fps }));
    }

    [Fact]
    public void Options_RequireAtLeastTwoPoolBuffers_AndAFailureBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsGraphicsCaptureService(new WindowsCaptureOptions { FramePoolBufferCount = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsGraphicsCaptureService(new WindowsCaptureOptions { MaxConsecutiveFrameFailures = 0 }));
    }

    [Fact]
    public void Options_DefaultsAreSensible()
    {
        var options = new WindowsCaptureOptions();

        Assert.Equal(30, options.MaxFramesPerSecond);
        Assert.Equal(2, options.FramePoolBufferCount);
        Assert.Equal(5, options.MaxConsecutiveFrameFailures);
    }
}
