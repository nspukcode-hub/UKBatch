using UKBatch.Runtime;
using UKBatch.Transport.Http.Endpoints;

namespace UKBatch.Transport.Http.Auth;

/// <summary>
/// HMAC nonce dedupe cache. Wraps <see cref="LruDedupeCache{TKey}"/> with capacity sized for
/// HMAC anti-replay (typically <c>MaxClockSkew × sustainable_rps</c>). The wrapper exists to give
/// DI a distinct type — sibling <see cref="MessageIdDedupeCache"/> uses the same primitive but with
/// a different capacity and a different purpose.
/// </summary>
/// <remarks>
/// <para><b>Sizing math:</b> with <c>MaxClockSkew = 300s</c> and default
/// <c>NonceCacheCapacity = 1024</c>, the cache can absorb <c>1024 / 300 ≈ 3.4 req/s</c> sustained
/// without a nonce getting LRU-evicted while still within the clock-skew replay window. Production
/// deployments exceeding that rate MUST size up.</para>
/// </remarks>
internal sealed class NonceDedupeCache
{
    private readonly LruDedupeCache<string> _cache;

    public NonceDedupeCache(int capacity)
    {
        _cache = new LruDedupeCache<string>(capacity, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns <c>true</c> on dedupe MISS (first time seeing this nonce — accepted);
    /// <c>false</c> on dedupe HIT (replay — reject).
    /// </summary>
    public bool TryAdd(string nonce) => _cache.TryAdd(nonce);

    /// <summary>Current entry count (diagnostic / test surface).</summary>
    public int Count => _cache.Count;
}
