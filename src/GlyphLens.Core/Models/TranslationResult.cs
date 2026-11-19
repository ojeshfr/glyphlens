namespace GlyphLens.Core.Models;

/// <summary>
/// The outcome of translating a piece of text with a specific local model.
/// </summary>
public sealed record TranslationResult
{
    public TranslationResult(
        string originalText,
        string translatedText,
        string sourceLanguage,
        string targetLanguage,
        string modelIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalText);
        ArgumentNullException.ThrowIfNull(translatedText);
        LanguageCode.Validate(sourceLanguage, nameof(sourceLanguage));
        LanguageCode.Validate(targetLanguage, nameof(targetLanguage));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentifier);

        OriginalText = originalText;
        TranslatedText = translatedText;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        ModelIdentifier = modelIdentifier;
    }

    public string OriginalText { get; }

    public string TranslatedText { get; }

    /// <summary>The source language that was actually used (given or detected).</summary>
    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    /// <summary>Identifies the local model (and version) that produced the translation, for caching and provenance.</summary>
    public string ModelIdentifier { get; }
}
