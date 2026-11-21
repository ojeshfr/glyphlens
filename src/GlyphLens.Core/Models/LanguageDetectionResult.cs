namespace GlyphLens.Core.Models;

/// <summary>Why a language could not be determined.</summary>
public enum LanguageDetectionIssue
{
    /// <summary>A language was determined.</summary>
    None = 0,

    /// <summary>The text is too short for the detector to decide.</summary>
    TooShort,

    /// <summary>The text has no letters (digits, symbols or whitespace only).</summary>
    NoText,

    /// <summary>The detector does not know the text's language, or could not separate the candidates.</summary>
    Unknown,
}

/// <summary>A language the detector considered, with the probability it assigned.</summary>
public sealed record LanguageCandidate
{
    public LanguageCandidate(string language, double confidence)
    {
        LanguageCode.Validate(language, nameof(language));
        if (double.IsNaN(confidence) || confidence < 0.0 || confidence > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be between 0 and 1.");
        }

        Language = language;
        Confidence = confidence;
    }

    public string Language { get; }

    public double Confidence { get; }
}

/// <summary>
/// The detector's verdict for one text. A detector is never infallible: callers must be prepared for
/// <see cref="Language"/> to be <see langword="null"/> and for low confidence.
/// </summary>
/// <remarks>
/// <see cref="Confidence"/> is the probability the detector itself reported, or <see langword="null"/> when the detector
/// reports none. It is never estimated or invented by GlyphLens.
/// </remarks>
public sealed record LanguageDetectionResult
{
    private LanguageDetectionResult(string? language, double? confidence, IReadOnlyList<LanguageCandidate> alternatives, LanguageDetectionIssue issue)
    {
        Language = language;
        Confidence = confidence;
        Alternatives = alternatives;
        Issue = issue;
    }

    /// <summary>The detected language code, or <see langword="null"/> when none could be determined.</summary>
    public string? Language { get; }

    /// <summary>The detector's probability for <see cref="Language"/> in [0, 1], or <see langword="null"/> when not reported.</summary>
    public double? Confidence { get; }

    /// <summary>Other plausible languages, best first (may be empty).</summary>
    public IReadOnlyList<LanguageCandidate> Alternatives { get; }

    public LanguageDetectionIssue Issue { get; }

    public static LanguageDetectionResult Detected(string language, double? confidence, IReadOnlyList<LanguageCandidate>? alternatives = null)
    {
        LanguageCode.Validate(language, nameof(language));
        if (confidence is { } c && (double.IsNaN(c) || c < 0.0 || c > 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must be between 0 and 1.");
        }

        return new LanguageDetectionResult(language, confidence, alternatives ?? [], LanguageDetectionIssue.None);
    }

    public static LanguageDetectionResult Undetermined(LanguageDetectionIssue issue, IReadOnlyList<LanguageCandidate>? alternatives = null)
    {
        if (issue == LanguageDetectionIssue.None)
        {
            throw new ArgumentException("An undetermined result needs a reason.", nameof(issue));
        }

        return new LanguageDetectionResult(null, null, alternatives ?? [], issue);
    }
}
