using GlyphLens.Core.Contracts;

namespace GlyphLens.Core.Tests.Contracts;

public class ProtocolVersionTests
{
    [Fact]
    public void Current_IsVersionOneZero()
    {
        Assert.Equal(new ProtocolVersion(1, 0), ProtocolVersion.Current);
        Assert.Equal("1.0", ProtocolVersion.Current.ToString());
    }

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("1.12", 1, 12)]
    [InlineData("10.3", 10, 3)]
    public void Parse_AcceptsMajorMinor(string text, int major, int minor)
    {
        var version = ProtocolVersion.Parse(text);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("0.1")]
    [InlineData("01.0")]
    [InlineData("1.01")]
    [InlineData("-1.0")]
    [InlineData("+1.0")]
    [InlineData(" 1.0")]
    [InlineData("1.0 ")]
    [InlineData("a.b")]
    [InlineData("1.")]
    [InlineData("99999999999.0")]
    public void TryParse_RejectsMalformedVersions(string? text)
    {
        Assert.False(ProtocolVersion.TryParse(text, out _));
    }

    [Fact]
    public void Parse_ThrowsFormatExceptionForMalformedVersion()
    {
        Assert.Throws<FormatException>(() => ProtocolVersion.Parse("v1"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void Constructor_RejectsInvalidComponents(int major, int minor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolVersion(major, minor));
    }

    [Theory]
    [InlineData("1.0", "1.0", true)]
    [InlineData("1.0", "1.5", true)]
    [InlineData("1.5", "1.0", true)]
    [InlineData("1.0", "2.0", false)]
    [InlineData("2.3", "1.3", false)]
    public void IsCompatibleWith_RequiresMatchingMajorVersion(string a, string b, bool expected)
    {
        Assert.Equal(expected, ProtocolVersion.Parse(a).IsCompatibleWith(ProtocolVersion.Parse(b)));
    }
}
