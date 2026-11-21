using GlyphLens.Core.Models;

namespace GlyphLens.Core.Translation;

/// <summary>What happened to one recognised region when it was offered for translation.</summary>
public enum RegionTranslationStatus
{
    /// <summary>The region was translated; <see cref="RegionTranslation.TranslatedText"/> is set.</summary>
    Translated = 1,

    /// <summary>The region is already in the target language (or the user chose the target as the source): nothing to translate.</summary>
    AlreadyTarget,

    /// <summary>The region has no letters (numbers, symbols): nothing to translate.</summary>
    NoText,

    /// <summary>The source language could not be determined reliably. The user can choose it manually.</summary>
    LanguageUncertain,

    /// <summary>No installed model translates this language pair; <see cref="RegionTranslation.RequiredPackage"/> says which would.</summary>
    ModelMissing,

    /// <summary>The engine does not support this language pair at all.</summary>
    UnsupportedLanguage,

    /// <summary>The Python worker is not running or did not answer.</summary>
    WorkerUnavailable,

    /// <summary>The translation engine could not be loaded.</summary>
    EngineUnavailable,

    /// <summary>Translation of this region failed.</summary>
    Failed,
}

/// <summary>
/// The result for one recognised region. The region keeps its identity and original coordinates, so the translation can be
/// mapped back to where the text is on screen.
/// </summary>
public sealed record RegionTranslation
{
    /// <summary>
    /// The region that was translated: a recognised line, or the merged region of a group of lines (the first member's id, the
    /// union of their bounds, the joined text). Frame-relative bounds.
    /// </summary>
    public required ScreenTextRegion Region { get; init; }

    /// <summary>The recognised lines the translation covers, each with its own identity and coordinates (one entry when nothing was merged).</summary>
    public IReadOnlyList<ScreenTextRegion> Members { get; init; } = [];

    /// <summary>Position of this result among the results of the pass, in reading order (0 = first).</summary>
    public int ReadingOrder { get; init; }

    public required RegionTranslationStatus Status { get; init; }

    /// <summary>The translated text when <see cref="Status"/> is <see cref="RegionTranslationStatus.Translated"/>; otherwise <see langword="null"/>.</summary>
    public string? TranslatedText { get; init; }

    /// <summary>The source language used, or <see langword="null"/> when it is unknown (uncertain).</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>Whether <see cref="SourceLanguage"/> was chosen by the user rather than detected.</summary>
    public bool SourceChosenByUser { get; init; }

    /// <summary>The detector's own probability for <see cref="SourceLanguage"/>, or <see langword="null"/> (never estimated).</summary>
    public double? DetectionConfidence { get; init; }

    /// <summary>Other languages the detector considered (best first); useful when the language is uncertain.</summary>
    public IReadOnlyList<LanguageCandidate> Alternatives { get; init; } = [];

    /// <summary>A short explanation for any status other than <see cref="RegionTranslationStatus.Translated"/>.</summary>
    public string? Message { get; init; }

    /// <summary>For <see cref="RegionTranslationStatus.ModelMissing"/>: the package that would make this translatable.</summary>
    public string? RequiredPackage { get; init; }

    /// <summary>Whether the translation came from the in-memory cache.</summary>
    public bool FromCache { get; init; }

    /// <summary>The model identity that produced the translation (the cache key's model component).</summary>
    public string? ModelVersion { get; init; }

    public Guid RegionId => Region.Id;

    public bool IsTranslated => Status == RegionTranslationStatus.Translated;
}

/// <summary>The outcome of one pass over a set of recognised regions.</summary>
/// <param name="Regions">One entry per input region, in input order.</param>
/// <param name="CacheHits">How many distinct texts were answered from the cache.</param>
/// <param name="TextsTranslated">How many distinct texts were sent to the translation engine.</param>
/// <param name="TextsDetected">How many distinct texts were sent to the language detector.</param>
public sealed record TranslationPassResult(IReadOnlyList<RegionTranslation> Regions, int CacheHits, int TextsTranslated, int TextsDetected);

/// <summary>Which languages a pass translates between.</summary>
public sealed record TranslationSelection
{
    public TranslationSelection(string? sourceLanguage, string targetLanguage)
    {
        if (sourceLanguage is not null)
        {
            LanguageCode.Validate(sourceLanguage, nameof(sourceLanguage));
        }

        LanguageCode.Validate(targetLanguage, nameof(targetLanguage));
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
    }

    /// <summary>The source language chosen by the user, or <see langword="null"/> to identify it automatically.</summary>
    public string? SourceLanguage { get; }

    public string TargetLanguage { get; }

    /// <summary>The default selection: identify the language automatically and translate to English.</summary>
    public static TranslationSelection AutoToEnglish { get; } = new(null, LanguageCode.English);
}

/// <summary>Tuning for <see cref="TranslationPipeline"/>.</summary>
public sealed record TranslationPipelineOptions
{
    /// <summary>
    /// A detected language is only trusted when the detector's own probability is at least this value. Detectors that report no
    /// probability are trusted as they are (a confidence is never invented).
    /// </summary>
    public double MinimumConfidence { get; init; } = 0.6;

    /// <summary>How many language detections are remembered (so unchanged text on screen is not identified again).</summary>
    public int DetectionCacheCapacity { get; init; } = 512;

    /// <summary>Maximum number of alternative languages asked of the detector.</summary>
    public int MaxAlternatives { get; init; } = 3;

    /// <summary>
    /// Decides which recognised lines are translated together as one text. <see langword="null"/> translates every region on its
    /// own; the application uses <see cref="ParagraphRegionGrouper"/> so a chat message or paragraph is translated with its context.
    /// </summary>
    public IRegionGrouper? Grouper { get; init; }
}
