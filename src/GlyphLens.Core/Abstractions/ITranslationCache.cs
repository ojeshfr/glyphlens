using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// Caches translations so repeated on-screen text is not re-translated.
/// </summary>
/// <remarks>Planned implementation: SQLite-backed cache in GlyphLens.Infrastructure.</remarks>
public interface ITranslationCache
{
    /// <summary>Returns the cached result for <paramref name="request"/>, or <see langword="null"/> on a miss.</summary>
    ValueTask<TranslationResult?> GetAsync(TranslationRequest request, CancellationToken cancellationToken);

    ValueTask StoreAsync(TranslationRequest request, TranslationResult result, CancellationToken cancellationToken);
}
