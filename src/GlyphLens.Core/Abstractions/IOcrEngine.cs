using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Recognises visually rendered text in captured pixels.
/// </summary>
/// <remarks>
/// <para>
/// Implementations: <c>WindowsOcrEngine</c> (the built-in Windows OCR, offline, in GlyphLens.Windows). Planned:
/// PaddleOCR inside the local Python worker, reached over IPC, as a multilingual second engine.
/// </para>
/// <para>
/// Returned regions have <see cref="DetectionSource.Ocr"/> and frame-relative pixel bounds (see
/// <see cref="ScreenTextRegion"/>). The caller keeps ownership of <c>frame</c>.
/// </para>
/// </remarks>
public interface IOcrEngine
{
    /// <exception cref="OcrException">The engine is unavailable, the language is not installed, or the frame cannot be processed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<IReadOnlyList<ScreenTextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken);
}

/// <summary>An OCR recogniser language: a BCP 47 tag and a display name.</summary>
public sealed record OcrLanguage(string Tag, string DisplayName);

/// <summary>
/// Lets the user pick which installed language an OCR engine recognises.
/// </summary>
public interface IOcrLanguageSource
{
    /// <summary>The languages the engine can recognise right now (empty when none is installed).</summary>
    IReadOnlyList<OcrLanguage> AvailableLanguages { get; }

    /// <summary>The language to recognise, or <see langword="null"/> to use the engine's default (the user's profile languages).</summary>
    string? SelectedLanguage { get; set; }
}
