namespace GlyphLens.Core.Models;

/// <summary>
/// A request to translate a piece of text into <see cref="TargetLanguage"/>.
/// </summary>
/// <remarks>
/// <see cref="SourceLanguage"/> is <see langword="null"/> when the source language should be detected.
/// </remarks>
public sealed record TranslationRequest
{
    public TranslationRequest(string text, string? sourceLanguage, string targetLanguage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (sourceLanguage is not null)
        {
            LanguageCode.Validate(sourceLanguage, nameof(sourceLanguage));
        }

        LanguageCode.Validate(targetLanguage, nameof(targetLanguage));

        Text = text;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
    }

    public string Text { get; }

    public string? SourceLanguage { get; }

    public string TargetLanguage { get; }
}
