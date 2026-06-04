using System.Collections.Concurrent;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;

namespace UKBatch.Transport.RabbitMQ.Dedupe;

/// <summary>
/// Receiver-side <see cref="JobMessage.MessageId"/> dedupe cache. On dedupe HIT the
/// cached <see cref="JobResult"/> is replayed (broker redelivery / sender retry: same MessageId,
/// re-delivered envelope → same result, the job does NOT re-run).
/// </summary>
/// <remarks>
/// <para><b>Self-contained:</b> this type embeds its own bounded LRU (the
/// <c>LinkedList&lt;string&gt;</c> + <c>Dictionary</c> + single-lock primitive mirrored from Core's
/// <c>LruDedupeCache&lt;TKey&gt;</c>) rather than reaching into Core's <c>internal</c> friend surface.
/// Copying ~30 lines keeps the RabbitMQ adapter OUT of Core's <c>InternalsVisibleTo</c> grant set.
/// The LRU here is intentionally tiny and stable.</para>
/// <para><b>Capacity (default 4096 — <see cref="RabbitMqTransportOptions.MessageIdCacheCapacity"/>):</b>
/// the MessageId lifetime spans broker redelivery and sender RPC retries. The LRU evicts MRU-overflow
/// keys and the matching <c>_results</c> entry is evicted in lock-step so both structures
/// stay bounded together. For v0.1 in-memory this is acceptable; persistent dedupe (<c>IJobStore</c>-backed)
/// is a future concern.</para>
/// <para><b>Known race window (future fix):</b> the <c>TryAdd</c> + <c>TryGetResult</c> +
/// <c>StoreResult</c> sequence is NOT atomic. Two concurrent deliveries of the same MessageId may both
/// observe TryAdd-MISS before either stores the result and dispatch the job twice. The window is bounded
/// by job dispatch latency; sender RPC retry intervals (2s+) make practical collisions rare. A future fix
/// would use a single atomic <c>TryReserveOrGetResult</c> backed by a <see cref="TaskCompletionSource{T}"/>.</para>
/// </remarks>
internal sealed class MessageIdDedupeCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<string>> _index;
    private readonly LinkedList<string> _order; // MRU at head, LRU at tail.
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, JobResult> _results;

    /// <summary>Constructs the cache with the given LRU capacity (must be &gt; 0).</summary>
    public MessageIdDedupeCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _index = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        _order = new LinkedList<string>();
        _results = new ConcurrentDictionary<string, JobResult>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns <c>true</c> on dedupe MISS (first sighting — caller processes the message then calls
    /// <see cref="StoreResult"/>). Returns <c>false</c> on dedupe HIT (caller must replay the cached
    /// <see cref="JobResult"/> via <see cref="TryGetResult"/> and ack WITHOUT re-running the job).
    /// </summary>
    public bool TryAdd(string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        lock (_lock)
        {
            if (_index.TryGetValue(messageId, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return false; // already seen (HIT)
            }

            var node = _order.AddFirst(messageId);
            _index[messageId] = node;
            if (_index.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _index.Remove(lru.Value);
                _results.TryRemove(lru.Value, out _); // couple result eviction to LRU → both structures stay bounded.
            }

            return true; // new (MISS)
        }
    }

    /// <summary>
    /// Retrieves the cached <see cref="JobResult"/> for a previously-seen MessageId. Returns
    /// <c>false</c> if it was never seen OR was seen but its result has not been stored yet (the
    /// race window documented on the type — caller proceeds without a cached replay).
    /// </summary>
    public bool TryGetResult(string messageId, out JobResult? result)
        => _results.TryGetValue(messageId, out result);

    /// <summary>
    /// Records the computed <see cref="JobResult"/> for a MessageId. Idempotent — re-storing replaces
    /// the prior value.
    /// </summary>
    public void StoreResult(string messageId, JobResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(result);
        _results[messageId] = result;
    }

    /// <summary>
    /// Removes a MessageId from BOTH the LRU index and the result store. Called by the consumer pump when
    /// processing threw AFTER <see cref="TryAdd"/> succeeded but BEFORE <see cref="StoreResult"/>:
    /// un-poisons the key so broker redelivery re-processes cleanly instead of hitting a resultless
    /// HIT that acks-without-running and silently drops the job.
    /// </summary>
    public void Evict(string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        lock (_lock)
        {
            if (_index.TryGetValue(messageId, out var node))
            {
                _order.Remove(node);
                _index.Remove(messageId);
            }
        }
        _results.TryRemove(messageId, out _);
    }

    /// <summary>Current LRU entry count (diagnostic / test surface).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _index.Count;
            }
        }
    }
}
