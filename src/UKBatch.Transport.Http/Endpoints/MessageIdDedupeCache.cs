using System.Collections.Concurrent;
using UKBatch.Abstractions.Models;

namespace UKBatch.Transport.Http.Endpoints;

/// <summary>
/// Receiver-side <c>JobMessage.MessageId</c> dedupe cache. On HIT, the cached
/// <see cref="JobResult"/> is replayed (sender retry semantics: same MessageId, retried envelope,
/// same result — the job does NOT re-run).
/// </summary>
/// <remarks>
/// <para><b>Self-contained bounded LRU:</b> a <c>LinkedList&lt;string&gt;</c> + <c>Dictionary</c> +
/// single-lock primitive, with the <c>_results</c> store evicted in lock-step with the LRU so BOTH
/// structures stay bounded together — neither grows without limit. (Kept self-contained rather than
/// reaching into Core's internal friend surface.)</para>
/// <para><b>Capacity (default 4096):</b> 4× the nonce cache because MessageId lifetime spans
/// sender-side retries (sender's Polly retry budget at default <c>RetryDelays = [2s, 5s, 15s]</c>
/// ≈ 22 seconds). Receiver-side MessageId must outlive that window so retried sends collapse to the
/// cached result.</para>
/// <para><b>Known race window (future fix):</b> the <c>TryAdd</c> + <c>TryGetResult</c> +
/// <c>StoreResult</c> sequence is NOT atomic. Under sender retry pressure (retry interval ≈ 2s; job
/// dispatch latency typically &lt; 100ms), two concurrent invokes with the same MessageId may both
/// observe a MISS and dispatch the job twice. The window is bounded by dispatch latency, so the
/// practical hit rate is low. A future fix would replace this with a single atomic
/// <c>TryReserveOrGetResult</c> protocol backed by
/// <see cref="System.Threading.Tasks.TaskCompletionSource{T}"/> so the second caller awaits the
/// first caller's result rather than re-dispatching. Until then, the idempotency contract holds for
/// sequential retries (sender's default 2s+ interval) but may double-execute under adversarial
/// concurrent retries.</para>
/// </remarks>
internal sealed class MessageIdDedupeCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<string>> _index;
    private readonly LinkedList<string> _order; // MRU at head, LRU at tail.
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, JobResult> _results;

    public MessageIdDedupeCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _index = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
        _order = new LinkedList<string>();
        _results = new ConcurrentDictionary<string, JobResult>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns <c>true</c> on dedupe MISS (first time we've seen this MessageId — caller must
    /// process the message then call <see cref="StoreResult"/>). Returns <c>false</c> on dedupe
    /// HIT (caller must replay the cached <see cref="JobResult"/> via <see cref="TryGetResult"/>).
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
    /// <c>false</c> if the MessageId was never seen OR was seen but the result has not yet been
    /// stored (the known race window — see <see cref="TryAdd"/> remarks).
    /// </summary>
    public bool TryGetResult(string messageId, out JobResult? result)
        => _results.TryGetValue(messageId, out result);

    /// <summary>
    /// Records the computed <see cref="JobResult"/> for a MessageId. Idempotent — re-storing the
    /// same MessageId replaces the prior value.
    /// </summary>
    public void StoreResult(string messageId, JobResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentNullException.ThrowIfNull(result);
        _results[messageId] = result;
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
