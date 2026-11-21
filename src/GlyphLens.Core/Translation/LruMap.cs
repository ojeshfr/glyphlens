namespace GlyphLens.Core.Translation;

/// <summary>A small bounded least-recently-used map. Not thread-safe: callers lock.</summary>
internal sealed class LruMap<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _index;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public LruMap(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _index = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
    }

    public int Count => _index.Count;

    public bool TryGet(TKey key, out TValue? value)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = default;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_index.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _index.Remove(key);
        }
        else if (_index.Count >= _capacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _index.Remove(oldest.Value.Key);
        }

        var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
        _order.AddFirst(node);
        _index[key] = node;
    }

    public void Clear()
    {
        _index.Clear();
        _order.Clear();
    }
}
