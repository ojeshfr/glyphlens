namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Identifies the language of a piece of text locally.
/// </summary>
/// <remarks>Planned implementation: local language identification inside the Python worker (Phase 3).</remarks>
public interface ILanguageDetector
{
    /// <summary>
    /// Returns a language code (see <see cref="Models.LanguageCode"/>), or <see langword="null"/>
    /// when the language cannot be determined with sufficient confidence.
    /// </summary>
    Task<string?> DetectLanguageAsync(string text, CancellationToken cancellationToken);
}
