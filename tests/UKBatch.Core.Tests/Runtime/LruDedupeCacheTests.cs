using FluentAssertions;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// <see cref="LruDedupeCache{TKey}"/> primitive contract gates.
/// </summary>
public class LruDedupeCacheTests
{
    [Fact]
    public void LruDedupeCache_TryAdd_OnSameKeyTwice_ReturnsFalseOnRepeat()
    {
        var cache = new LruDedupeCache<string>(capacity: 10, StringComparer.Ordinal);
        cache.TryAdd("a").Should().BeTrue("first insertion is a dedupe MISS");
        cache.TryAdd("a").Should().BeFalse("second insertion is a dedupe HIT — already-present.");
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void LruDedupeCache_AtCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new LruDedupeCache<string>(capacity: 3, StringComparer.Ordinal);
        cache.TryAdd("a").Should().BeTrue();
        cache.TryAdd("b").Should().BeTrue();
        cache.TryAdd("c").Should().BeTrue();
        cache.Count.Should().Be(3);

        // Adding a 4th key evicts the LRU key (which is "a", since b and c are more recent).
        cache.TryAdd("d").Should().BeTrue();
        cache.Count.Should().Be(3, "capacity is enforced.");

        // Re-adding "a" is now a MISS again (it was evicted).
        cache.TryAdd("a").Should().BeTrue("evicted key treated as new on reinsert.");

        // "b" is now the LRU. Adding "e" evicts "b".
        cache.TryAdd("e").Should().BeTrue();
        cache.TryAdd("b").Should().BeTrue("evicted by adding 'e'.");
    }

    [Fact]
    public void LruDedupeCache_TouchOnHit_MovesKeyToMru()
    {
        // Capacity 3. Insert a, b, c. Touch "a" (TryAdd hit). Then insert "d" — "b" (LRU) must
        // be evicted, NOT "a" (which is now MRU after touch).
        var cache = new LruDedupeCache<string>(capacity: 3, StringComparer.Ordinal);
        cache.TryAdd("a");
        cache.TryAdd("b");
        cache.TryAdd("c");
        cache.TryAdd("a").Should().BeFalse("HIT, but the touch moves 'a' to MRU.");
        cache.TryAdd("d").Should().BeTrue();
        // "b" must be evicted, NOT "a".
        cache.TryAdd("a").Should().BeFalse("'a' is still in cache after touch + insert of 'd'.");
        cache.TryAdd("b").Should().BeTrue("'b' was evicted as the LRU.");
    }

    [Fact]
    public void Ctor_ZeroOrNegativeCapacity_Throws()
    {
        FluentActions.Invoking(() => new LruDedupeCache<string>(capacity: 0))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new LruDedupeCache<string>(capacity: -1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryAdd_NullKey_Throws()
    {
        var cache = new LruDedupeCache<string>(capacity: 5);
        FluentActions.Invoking(() => cache.TryAdd(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryAdd_ManyConcurrentThreads_StaysWithinCapacity()
    {
        // Concurrency smoke: 8 threads inserting 1000 random keys each into a cache of capacity 100.
        // After all inserts, Count must equal capacity exactly.
        var cache = new LruDedupeCache<int>(capacity: 100);
        Parallel.For(0, 8, threadId =>
        {
            var rng = new Random(threadId);
            for (var i = 0; i < 1000; i++)
            {
                cache.TryAdd(rng.Next(0, 10_000));
            }
        });
        cache.Count.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void LruDedupeCache_TenThousandAndOneInserts_FirstKeyEvicted()
    {
        // stress test the LRU eviction at the production capacity (10_000).
        var cache = new LruDedupeCache<string>(capacity: 10_000, StringComparer.Ordinal);
        for (var i = 0; i < 10_001; i++)
        {
            cache.TryAdd($"key-{i:D6}").Should().BeTrue();
        }
        cache.Count.Should().Be(10_000);
        // First key was evicted at the 10_001st insert.
        cache.TryAdd("key-000000").Should().BeTrue("first key (LRU) was evicted at capacity overflow.");
        // The last-inserted key was MRU; re-adding is a HIT.
        cache.TryAdd("key-010000").Should().BeFalse("last key still in cache.");
    }
}
