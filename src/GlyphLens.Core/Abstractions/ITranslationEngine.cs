using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Translates text using locally installed models only.
/// </summary>
/// <remarks>
/// Planned implementation: Argos Translate inside the Python worker (Phase 4).
/// Implementations must never fall back to a cloud service.
/// </remarks>
public interface ITranslationEngine
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
