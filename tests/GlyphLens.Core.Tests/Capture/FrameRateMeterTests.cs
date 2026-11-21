using GlyphLens.Core.Capture;

namespace GlyphLens.Core.Tests.Capture;

public sealed class FrameRateMeterTests
{
    private static long Seconds(double s) => (long)(s * TimeSpan.TicksPerSecond);

    [Fact]
    public void TheFirstSample_HasNoRateYet() =>
        Assert.Null(new FrameRateMeter(TimeSpan.FromSeconds(2)).Add(10, Seconds(0)));

    [Fact]
    public void AConstantRate_IsMeasured()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(2));
        _ = meter.Add(0, Seconds(0));

        double? rate = null;
        for (int i = 1; i <= 6; i++)
        {
            rate = meter.Add(i * 30, Seconds(i)); // 30 frames per second
        }

        Assert.Equal(30.0, rate!.Value, 6);
    }

    [Fact]
    public void AStaticScreen_MeasuresZero()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(2));
        _ = meter.Add(100, Seconds(0));

        Assert.Equal(0.0, meter.Add(100, Seconds(1))!.Value);
    }

    [Fact]
    public void OnlyTheWindowCounts_SoARateChangeIsFollowed()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(2));
        _ = meter.Add(0, Seconds(0));
        for (int i = 1; i <= 5; i++)
        {
            _ = meter.Add(i * 60, Seconds(i)); // 60 fps for five seconds
        }

        double? rate = null;
        for (int i = 6; i <= 10; i++)
        {
            rate = meter.Add(300 + ((i - 5) * 5), Seconds(i)); // then 5 fps
        }

        Assert.Equal(5.0, rate!.Value, 6);
    }

    [Fact]
    public void ACounterThatGoesBack_StartsANewMeasurement()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(2));
        _ = meter.Add(500, Seconds(0));
        _ = meter.Add(530, Seconds(1));

        Assert.Null(meter.Add(3, Seconds(2)));
        Assert.Equal(10.0, meter.Add(13, Seconds(3))!.Value, 6);
    }

    [Fact]
    public void Reset_ForgetsTheSamples()
    {
        var meter = new FrameRateMeter(TimeSpan.FromSeconds(2));
        _ = meter.Add(0, Seconds(0));
        _ = meter.Add(30, Seconds(1));

        meter.Reset();

        Assert.Null(meter.Add(60, Seconds(2)));
    }

    [Fact]
    public void ANonPositiveWindow_IsRejected() =>
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateMeter(TimeSpan.Zero));
}
