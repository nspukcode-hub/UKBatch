using FluentAssertions;
using UKBatch.Transport.Http.Auth;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Auth;

/// <summary>
/// Replay-prevention LRU cache for HMAC nonces.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class NonceDedupeCacheTests
{
    [Fact]
    public void TryAdd_NewNonce_ReturnsTrue()
    {
        var cache = new NonceDedupeCache(capacity: 16);
        cache.TryAdd("nonce-1").Should().BeTrue();
    }

    [Fact]
    public void TryAdd_DuplicateNonce_ReturnsFalse()
    {
        var cache = new NonceDedupeCache(capacity: 16);
        cache.TryAdd("nonce-x").Should().BeTrue();
        cache.TryAdd("nonce-x").Should().BeFalse();
    }

    [Fact]
    public void TryAdd_AboveCapacity_LRUEvictsOldest()
    {
        var cache = new NonceDedupeCache(capacity: 3);
        cache.TryAdd("n1").Should().BeTrue();   // MRU: n1
        cache.TryAdd("n2").Should().BeTrue();   // MRU: n2, n1
        cache.TryAdd("n3").Should().BeTrue();   // MRU: n3, n2, n1
        cache.Count.Should().Be(3);

        // Adding n4 evicts the LRU (n1).
        cache.TryAdd("n4").Should().BeTrue();   // MRU: n4, n3, n2 (n1 evicted)
        cache.Count.Should().Be(3);

        // n1 should now be re-addable (was evicted).
        cache.TryAdd("n1").Should().BeTrue();   // MRU: n1, n4, n3 (n2 evicted)

        // n3 is still present (added third, not LRU when n1 came back).
        cache.TryAdd("n3").Should().BeFalse();
    }

    [Fact]
    public async Task TryAdd_ConcurrentSameNonce_OnlyOneReturnsTrue()
    {
        var cache = new NonceDedupeCache(capacity: 256);
        const string nonce = "concurrent-nonce";
        const int Threads = 32;
        var trueCount = 0;
        var falseCount = 0;
        var barrier = new Barrier(Threads);
        var tasks = Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            if (cache.TryAdd(nonce))
            {
                Interlocked.Increment(ref trueCount);
            }
            else
            {
                Interlocked.Increment(ref falseCount);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        trueCount.Should().Be(1, "only one thread should observe MISS for the same nonce");
        falseCount.Should().Be(Threads - 1);
    }
}
