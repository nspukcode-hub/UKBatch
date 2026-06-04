using System.Collections.Concurrent;
using UKBatch.Abstractions.Models;
using UKBatch.Runtime;

namespace UKBatch.Transport.Http.Endpoints;

/// <summary>
/// Receiver-side <c>JobMessage.MessageId</c> dedupe cache. On HIT, the cached
/// <see cref="JobResult"/> is replayed (sender retry semantics: same MessageId, retried envelope,
/// same result). Composes <see cref="LruDedupeCache{TKey}"/> for the LRU eviction signal; pairs
/// with <see cref="ConcurrentDictionary{TKey,TValue}"/> for the result lookup.
/// </summary>
/// <remarks>
/// <para><b>Capacity (default 4096):</b> 4× the nonce cache because MessageId lifetime spans
/// sender-side retries (sender's Polly retry budget at default
/// <c>RetryDelays = [2s, 5s, 15s]</c> ≈ 22 seconds of retry window). Receiver-side MessageId must
/// outlive that window so retried sends collapse to the cached result.</para>
/// <para><b>Memory growth (known trade-off):</b> the <c>_results</c> dictionary is NOT bounded —
/// it grows monotonically with each new MessageId processed. The <c>_cache</c> LRU evicts MRU keys
/// above capacity, but <c>_results</c> entries leak until process restart. For v0.1 this is an
/// acceptable trade-off: MessageId lifetime is bounded by the sender's retry budget (~22s default);
/// the leak rate at Sample.CrossServiceHttp traffic (~1 req/s) is ~5K entries/hour. Production
/// deployments at &gt;10 req/s sustained should monitor <c>_results</c> growth.
/// A future fix would add an LRU-eviction event hook so <c>_results.TryRemove(evictedKey)</c> fires
/// on each LRU drop. That requires extending <see cref="LruDedupeCache{TKey}"/> with a callback API.</para>
/// </remarks>
internal sealed class MessageIdDedupeCache
{
    private readonly LruDedupeCache<string> _cache;
    private readonly ConcurrentDictionary<string, JobResult> _results;

    public MessageIdDedupeCache(int capacity)
    {
        _cache = new LruDedupeCache<string>(capacity, StringComparer.Ordinal);
        _results = new ConcurrentDictionary<string, JobResult>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns <c>true</c> on dedupe MISS (first time we've seen this MessageId — caller must
    /// process the message then call <see cref="StoreResult"/>). Returns <c>false</c> on dedupe
    /// HIT (caller must replay the cached <see cref="JobResult"/> via <see cref="TryGetResult"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Known race window (future fix):</b> the <c>TryAdd</c> + <c>TryGetResult</c>
    /// + <c>StoreResult</c> sequence is NOT atomic. Under sender retry pressure (Polly retry interval
    /// ≈ 2s; job dispatch latency typically &lt; 100ms), two concurrent invokes with the same
    /// MessageId may both see TryAdd-MISS-then-MISS-but-no-result and dispatch the job twice. The
    /// race window is bounded by job dispatch latency — typically &lt; 100ms — so practical hit rate
    /// is low. A future fix would replace this with a single atomic <c>TryReserveOrGetResult</c> protocol backed
    /// by <see cref="System.Threading.Tasks.TaskCompletionSource{T}"/> so the second caller awaits
    /// the first caller's result rather than re-dispatching. Until then, idempotency contract holds
    /// for sequential retries (sender's default 2s+ retry interval) but may double-execute under
    /// adversarial concurrent retries.</para>
    /// </remarks>
    public bool TryAdd(string messageId) => _cache.TryAdd(messageId);

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
        _results.AddOrUpdate(messageId, result, (_, _) => result);
    }

    /// <summary>Current LRU entry count (diagnostic / test surface).</summary>
    public int Count => _cache.Count;
}
