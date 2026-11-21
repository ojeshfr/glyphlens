using GlyphLens.Infrastructure.Translation;

namespace GlyphLens.Infrastructure.Tests.Translation;

public class TranslationClientOptionsTests
{
    [Fact]
    public void Defaults_FollowTheProtocolDocument()
    {
        var options = new TranslationClientOptions();

        Assert.Equal(TimeSpan.FromSeconds(15), options.DetectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.TranslateTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ModelsTimeout);
        Assert.Equal(TimeSpan.FromSeconds(180), options.InstallTimeout);
        Assert.Equal(8, options.ChunkSize);
        Assert.Equal(4096, options.MaxTextLength);
        Assert.Equal(64, TranslationClientOptions.MaxDetectBatchSize);
        Assert.Equal(32, TranslationClientOptions.MaxTranslateChunkSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void ChunkSize_AcceptsTheRangeOneToThirtyTwo(int value)
    {
        Assert.Equal(value, new TranslationClientOptions { ChunkSize = value }.ChunkSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    [InlineData(int.MaxValue)]
    public void ChunkSize_RejectsValuesOutsideOneToThirtyTwo(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationClientOptions { ChunkSize = value });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void MaxTextLength_AcceptsTheRangeOneToTheWorkersLimit(int value)
    {
        Assert.Equal(value, new TranslationClientOptions { MaxTextLength = value }.MaxTextLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(4097)]
    public void MaxTextLength_RejectsValuesOutsideTheWorkersLimit(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationClientOptions { MaxTextLength = value });
    }

    [Fact]
    public void Timeouts_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationClientOptions { DetectTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationClientOptions { TranslateTimeout = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationClientOptions { ModelsTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationClientOptions { InstallTimeout = TimeSpan.FromMilliseconds(-1) });
    }

    [Fact]
    public void WithExpression_KeepsTheOtherValuesAndStillValidates()
    {
        var changed = new TranslationClientOptions() with { ChunkSize = 4 };

        Assert.Equal(4, changed.ChunkSize);
        Assert.Equal(TimeSpan.FromSeconds(180), changed.InstallTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => changed with { ChunkSize = 99 });
    }
}
