namespace UKBatch.Runtime;

/// <summary>
/// Bounded LRU dedupe cache. Keys are added MRU-first; on capacity overflow the LRU key is evicted.
/// Lookup-and-touch is O(1) amortized; thread-safe via a single internal lock.
/// </summary>
/// <typeparam name="TKey">Non-null key type.</typeparam>
/// <remarks>
/// <para>The primitive backing <c>JobStatusHubFanout._completedBatches</c>.
/// The same primitive is consumed by the dashboard's <c>RestUKBatchClient</c> for SignalR
/// <c>(ExecutionId, Status, AttemptNumber)</c> client-side dedupe.</para>
/// <para><b>TryAdd return semantics:</b> mirrors <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryAdd"/>
/// — returns <c>true</c> on dedupe MISS (key was new, added), <c>false</c> on dedupe HIT (key already
/// present; the existing node is touched as MRU).</para>
/// <para><b>Concurrency:</b> all operations serialize on <c>_lock</c>. For v0.1 in-process loads
/// the lock is uncontested; v0.2 may swap in a striped lock if telemetry surfaces contention.</para>
/// </remarks>
internal sealed class LruDedupeCache<TKey> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _index;
    private readonly LinkedList<TKey> _order;   // MRU at head, LRU at tail.
    private readonly object _lock = new();

    /// <summary>Constructs an LRU dedupe cache with the given capacity and key comparer.</summary>
    /// <param name="capacity">Max keys retained; must be &gt; 0.</param>
    /// <param name="comparer">Optional equality comparer for keys; defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    public LruDedupeCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _index = new Dictionary<TKey, LinkedListNode<TKey>>(comparer);
        _order = new LinkedList<TKey>();
    }

    /// <summary>
    /// Returns <c>true</c> if the key was new (dedupe MISS — added to cache); <c>false</c> if
    /// already present (dedupe HIT — existing node touched as MRU).
    /// </summary>
    public bool TryAdd(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return false; // already seen
            }
            var node = _order.AddFirst(key);
            _index[key] = node;
            if (_index.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _index.Remove(lru.Value);
            }
            return true;
        }
    }

    /// <summary>Current number of keys retained.</summary>
    public int Count { get { lock (_lock) { return _index.Count; } } }
}
