using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Translates text using locally installed models only.
/// </summary>
/// <remarks>
/// Implementation: Argos Translate inside the Python worker, reached over IPC. Implementations must never fall back to a
/// cloud service and must never invent a translation: when a model is missing the outcome says so.
/// </remarks>
public interface ITranslationEngine
{
    /// <summary>Translates one text.</summary>
    /// <exception cref="TranslationException">The text could not be translated (the error says why).</exception>
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Translates several texts in one round trip. The result has one outcome per request, in order; a failure for one
    /// text does not fail the others.
    /// </summary>
    /// <exception cref="TranslationException">The whole batch failed (for example the worker is unavailable).</exception>
    Task<IReadOnlyList<TranslationOutcome>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken);
}
