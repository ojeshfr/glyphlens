using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public class DpiScaleTests
{
    [Theory]
    [InlineData(96, 100, 1.0)]
    [InlineData(120, 125, 1.25)]
    [InlineData(144, 150, 1.5)]
    [InlineData(192, 200, 2.0)]
    [InlineData(288, 300, 3.0)]
    public void FromDpi_ProducesTheStandardWindowsScaleFactors(double dpi, int percent, double factor)
    {
        var scale = DpiScale.FromDpi(dpi);

        Assert.Equal(percent, scale.Percent);
        Assert.Equal(factor, scale.Factor, 6);
    }

    [Theory]
    [InlineData(1.0, 1920, 1920)]
    [InlineData(1.25, 1920, 1536)]
    [InlineData(1.5, 1920, 1280)]
    [InlineData(2.0, 3840, 1920)]
    public void PhysicalToDip_DividesByTheScale(double factor, double physical, double expectedDip)
    {
        Assert.Equal(expectedDip, new DpiScale(factor).PhysicalToDip(physical), 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void DipAndPhysical_RoundTrip(double factor)
    {
        var scale = new DpiScale(factor);

        Assert.Equal(1234.5, scale.PhysicalToDip(scale.DipToPhysical(1234.5)), 9);
    }

    [Fact]
    public void PhysicalPixelsAndDips_AreNotInterchangeableAbove100Percent()
    {
        var scale = new DpiScale(1.5);

        Assert.NotEqual(900.0, scale.PhysicalToDip(900.0));
        Assert.Equal(600.0, scale.PhysicalToDip(900.0));
    }

    [Fact]
    public void MixedScalesAcrossDisplays_ConvertIndependently()
    {
        var laptop = new DpiScale(1.5); // 150 % panel
        var external = new DpiScale(1.0); // 100 % monitor

        Assert.Equal(640.0, laptop.PhysicalToDip(960.0));
        Assert.Equal(960.0, external.PhysicalToDip(960.0));
    }

    [Fact]
    public void Identity_Is100Percent()
    {
        Assert.Equal(100, DpiScale.Identity.Percent);
        Assert.Equal("100%", DpiScale.Identity.ToString());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.1)]
    [InlineData(17.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_RejectsImplausibleFactors(double factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DpiScale(factor));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-96.0)]
    [InlineData(double.NaN)]
    public void FromDpi_RejectsNonPositiveDpi(double dpi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DpiScale.FromDpi(dpi));
    }
}
