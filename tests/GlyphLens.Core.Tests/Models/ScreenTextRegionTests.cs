using GlyphLens.Core.Models;

namespace GlyphLens.Core.Tests.Models;

public class ScreenTextRegionTests
{
    private static readonly Guid SampleId = new("3f2b8c1e-6a4d-4f0e-9b7a-2c5d8e1f0a93");
    private static readonly DateTimeOffset SampleTime = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static ScreenTextRegion Create(
        Guid? id = null,
        string text = "Bonjour",
        int x = 10,
        int y = 20,
        int width = 120,
        int height = 24,
        DetectionSource source = DetectionSource.Ocr,
        double confidence = 0.92,
        string? language = "fr") =>
        new(id ?? SampleId, text, x, y, width, height, source, confidence, language, SampleTime);

    [Fact]
    public void Constructor_WithValidValues_SetsAllProperties()
    {
        var region = Create();

        Assert.Equal(SampleId, region.Id);
        Assert.Equal("Bonjour", region.Text);
        Assert.Equal(10, region.X);
        Assert.Equal(20, region.Y);
        Assert.Equal(120, region.Width);
        Assert.Equal(24, region.Height);
        Assert.Equal(DetectionSource.Ocr, region.DetectionSource);
        Assert.Equal(0.92, region.Confidence);
        Assert.Equal("fr", region.SourceLanguage);
        Assert.Equal(SampleTime, region.Timestamp);
        Assert.Equal(new ScreenRectangle(10, 20, 120, 24), region.Bounds);
    }

    [Fact]
    public void Constructor_AllowsNegativeCoordinatesForMultiMonitorLayouts()
    {
        var region = Create(x: -1920, y: -200);

        Assert.Equal(-1920, region.X);
        Assert.Equal(-200, region.Y);
    }

    [Fact]
    public void Constructor_AllowsUnknownSourceLanguage()
    {
        Assert.Null(Create(language: null).SourceLanguage);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Constructor_AcceptsConfidenceBoundaries(double confidence)
    {
        Assert.Equal(confidence, Create(confidence: confidence).Confidence);
    }

    [Fact]
    public void Constructor_RejectsEmptyId()
    {
        var ex = Assert.Throws<ArgumentException>(() => Create(id: Guid.Empty));
        Assert.Equal("id", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankText(string text)
    {
        Assert.Throws<ArgumentException>(() => Create(text: text));
    }

    [Fact]
    public void Constructor_RejectsNullText()
    {
        Assert.Throws<ArgumentNullException>(() => Create(text: null!));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void Constructor_RejectsNonPositiveSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(width: width, height: height));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Constructor_RejectsConfidenceOutsideUnitRange(double confidence)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Create(confidence: confidence));
        Assert.Equal("confidence", ex.ParamName);
    }

    [Fact]
    public void Constructor_RejectsUndefinedDetectionSource()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(source: (DetectionSource)99));
    }

    [Fact]
    public void Constructor_RejectsInvalidSourceLanguage()
    {
        var ex = Assert.Throws<ArgumentException>(() => Create(language: "French"));
        Assert.Equal("sourceLanguage", ex.ParamName);
    }

    [Fact]
    public void Records_WithSameValues_AreEqual()
    {
        Assert.Equal(Create(), Create());
        Assert.NotEqual(Create(), Create(text: "Hola"));
    }
}
