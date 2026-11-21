using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Identifies the language of texts locally.
/// </summary>
/// <remarks>
/// Implementation: a local library inside the Python worker, reached over IPC (<c>language.detect</c>). It makes no
/// network requests. A detector is not infallible: very short or mixed text yields low confidence or no answer, and
/// callers let the user override the language.
/// </remarks>
public interface ILanguageDetector
{
    /// <summary>
    /// Detects the language of each text. The result has one entry per input, in order.
    /// </summary>
    /// <exception cref="WorkerException">The worker is unavailable or failed.</exception>
    Task<IReadOnlyList<LanguageDetectionResult>> DetectAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
