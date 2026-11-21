using GlyphLens.Core.Models;

namespace GlyphLens.Core.Abstractions;

/// <summary>
/// The identity of a cached translation. The model version is part of the key, so installing or updating a model makes
/// earlier results unreachable instead of stale.
/// </summary>
/// <param name="NormalizedText">The text after <c>TextNormalizer.Normalize</c>.</param>
/// <param name="SourceLanguage">The source language used.</param>
/// <param name="TargetLanguage">The target language.</param>
/// <param name="ModelVersion">The model identity from the route status.</param>
public sealed record TranslationCacheKey(string NormalizedText, string SourceLanguage, string TargetLanguage, string ModelVersion);

/// <summary>
/// Remembers translations so unchanged on-screen text is not translated again.
/// </summary>
/// <remarks>
/// The implementation is a bounded in-memory LRU. Cached text is private screen content: it is never persisted by
/// default and is cleared on request.
/// </remarks>
public interface ITranslationCache
{
    int Count { get; }

    bool TryGet(TranslationCacheKey key, out TranslationResult? result);

    void Store(TranslationCacheKey key, TranslationResult result);

    /// <summary>Removes every entry produced by <paramref name="modelVersion"/>.</summary>
    void InvalidateModel(string modelVersion);

    /// <summary>Removes everything.</summary>
    void Clear();
}
