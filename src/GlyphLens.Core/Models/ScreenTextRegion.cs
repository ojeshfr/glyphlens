namespace GlyphLens.Core.Models;

/// <summary>
/// A piece of text detected on screen, with its location and provenance.
/// </summary>
/// <remarks>
/// Position and size are in virtual-screen physical pixels (see <see cref="ScreenRectangle"/>).
/// <see cref="SourceLanguage"/> is <see langword="null"/> until language detection has run.
/// </remarks>
public sealed record ScreenTextRegion
{
    public ScreenTextRegion(
        Guid id,
        string text,
        int x,
        int y,
        int width,
        int height,
        DetectionSource detectionSource,
        double confidence,
        string? sourceLanguage,
        DateTimeOffset timestamp)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Region id must not be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (!Enum.IsDefined(detectionSource))
        {
            throw new ArgumentOutOfRangeException(nameof(detectionSource), detectionSource, "Unknown detection source.");
        }

        if (double.IsNaN(confidence) || confidence < 0.0 || confidence > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be between 0 and 1.");
        }

        if (sourceLanguage is not null)
        {
            LanguageCode.Validate(sourceLanguage, nameof(sourceLanguage));
        }

        Id = id;
        Text = text;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        DetectionSource = detectionSource;
        Confidence = confidence;
        SourceLanguage = sourceLanguage;
        Timestamp = timestamp;
    }

    public Guid Id { get; }

    public string Text { get; }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public DetectionSource DetectionSource { get; }

    /// <summary>Detector confidence in the range [0, 1].</summary>
    public double Confidence { get; }

    public string? SourceLanguage { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>The region's bounds as a <see cref="ScreenRectangle"/>.</summary>
    public ScreenRectangle Bounds => new(X, Y, Width, Height);
}
