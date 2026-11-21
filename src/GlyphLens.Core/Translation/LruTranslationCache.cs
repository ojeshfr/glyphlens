using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;

namespace GlyphLens.Core.Translation;

/// <summary>
/// A thread-safe, bounded, in-memory least-recently-used cache of translations.
/// </summary>
/// <remarks>
/// <para>
/// Cached text is private screen content: the cache lives in memory only, is never persisted, and is emptied by
/// <see cref="Clear"/>. It is bounded by entry count, so it cannot grow without limit while the user keeps reading new text.
/// </para>
/// <para>
/// Recency: <see cref="TryGet"/> counts as a use and refreshes the entry; <see cref="Store"/> of a key that is already
/// present replaces the value and refreshes it too. When the capacity is exceeded the least recently used entry is dropped.
/// </para>
/// <para>
/// Keys are compared with the record's value equality, which for strings is ordinal and case-sensitive: the text's letter
/// case matters (it can change a translation), and language codes and model versions are compared exactly as given.
/// Callers use the lowercase codes the worker reports and <see cref="TextNormalizer.Normalize"/>d text, so no further
/// folding is done here.
/// </para>
/// <para>All members are safe to call from any thread; each operation holds one short lock and does no I/O.</para>
/// </remarks>
public sealed class LruTranslationCache : ITranslationCache
{
    /// <summary>The capacity used when none is given.</summary>
    public const int DefaultCapacity = 512;

    private readonly object _gate = new();
    private readonly Dictionary<TranslationCacheKey, LinkedListNode<Entry>> _map;

    // Most recently used first.
    private readonly LinkedList<Entry> _order = new();

    /// <summary>Creates a cache that keeps at most <paramref name="capacity"/> translations.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public LruTranslationCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);

        Capacity = capacity;
        _map = new Dictionary<TranslationCacheKey, LinkedListNode<Entry>>(Math.Min(capacity, 1024));
    }

    /// <summary>The most entries the cache keeps.</summary>
    public int Capacity { get; }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(TranslationCacheKey key, out TranslationResult? result)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                MoveToFront(node);
                result = node.Value.Result;
                return true;
            }
        }

        result = null;
        return false;
    }

    /// <inheritdoc />
    public void Store(TranslationCacheKey key, TranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value.Result = result;
                MoveToFront(existing);
                return;
            }

            _map[key] = _order.AddFirst(new Entry(key, result));
            if (_map.Count > Capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    /// <inheritdoc />
    public void InvalidateModel(string modelVersion)
    {
        ArgumentNullException.ThrowIfNull(modelVersion);

        lock (_gate)
        {
            var node = _order.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Key.ModelVersion, modelVersion, StringComparison.Ordinal))
                {
                    _order.Remove(node);
                    _map.Remove(node.Value.Key);
                }

                node = next;
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    // Must be called with the lock held.
    private void MoveToFront(LinkedListNode<Entry> node)
    {
        if (!ReferenceEquals(_order.First, node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }

    private sealed class Entry(TranslationCacheKey key, TranslationResult result)
    {
        public TranslationCacheKey Key { get; } = key;

        public TranslationResult Result { get; set; } = result;
    }
}
