using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Transport.RabbitMQ.Dedupe;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Dedupe;

/// <summary>
/// <see cref="MessageIdDedupeCache"/> unit coverage. Locks: TryAdd MISS/HIT semantics,
/// StoreResult/TryGetResult, the LRU↔results coupling (both structures stay bounded
/// together), MRU touch, and — critically — the <see cref="MessageIdDedupeCache.Evict"/>
/// "un-poison" path (after Evict the same MessageId is a MISS again so redelivery re-runs the job).
/// Docker-free.
/// </summary>
public sealed class MessageIdDedupeCacheTests
{
    private static JobResult Result(string execId, JobStatus status = JobStatus.Completed) => new()
    {
        ExecutionId = execId,
        Status = status,
        CompletedAtUtc = DateTimeOffset.UtcNow,
    };

    // ===== ctor guards =====

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_NonPositiveCapacity_Throws(int capacity)
    {
        var act = () => new MessageIdDedupeCache(capacity);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ctor_PositiveCapacity_StartsEmpty()
    {
        new MessageIdDedupeCache(16).Count.Should().Be(0);
    }

    // ===== TryAdd MISS / HIT =====

    [Fact]
    public void TryAdd_FirstSighting_ReturnsTrue_Miss()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1").Should().BeTrue("first sighting is a MISS");
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void TryAdd_SecondSighting_ReturnsFalse_Hit()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1").Should().BeTrue();
        cache.TryAdd("m1").Should().BeFalse("second sighting is a HIT");
        cache.Count.Should().Be(1, "a HIT does not add a second entry");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TryAdd_NullOrEmptyMessageId_Throws(string? messageId)
    {
        var cache = new MessageIdDedupeCache(16);
        var act = () => cache.TryAdd(messageId!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryAdd_DistinctIds_AllMiss()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("a").Should().BeTrue();
        cache.TryAdd("b").Should().BeTrue();
        cache.TryAdd("c").Should().BeTrue();
        cache.Count.Should().Be(3);
    }

    [Fact]
    public void TryAdd_OrdinalComparison_CaseSensitive()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("Msg").Should().BeTrue();
        cache.TryAdd("msg").Should().BeTrue("ordinal comparison treats different casing as distinct ids");
        cache.Count.Should().Be(2);
    }

    // ===== StoreResult / TryGetResult =====

    [Fact]
    public void StoreResult_ThenTryGetResult_ReturnsStored()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1");
        cache.StoreResult("m1", Result("exec-1"));

        cache.TryGetResult("m1", out var fetched).Should().BeTrue();
        fetched!.ExecutionId.Should().Be("exec-1");
        fetched.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void TryGetResult_NeverStored_ReturnsFalse()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1");
        cache.TryGetResult("m1", out var fetched).Should().BeFalse(
            "the in-flight race: seen but not yet stored returns no cached replay");
        fetched.Should().BeNull();
    }

    [Fact]
    public void TryGetResult_NeverSeen_ReturnsFalse()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryGetResult("ghost", out var fetched).Should().BeFalse();
        fetched.Should().BeNull();
    }

    [Fact]
    public void StoreResult_FailedStatus_RoundTrips()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1");
        cache.StoreResult("m1", Result("exec-f", JobStatus.Failed) with { ErrorMessage = "boom" });

        cache.TryGetResult("m1", out var fetched).Should().BeTrue();
        fetched!.Status.Should().Be(JobStatus.Failed);
        fetched.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public void StoreResult_ReStore_ReplacesPriorValue()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1");
        cache.StoreResult("m1", Result("exec-old"));
        cache.StoreResult("m1", Result("exec-new"));
        cache.TryGetResult("m1", out var fetched).Should().BeTrue();
        fetched!.ExecutionId.Should().Be("exec-new");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void StoreResult_NullOrEmptyMessageId_Throws(string? messageId)
    {
        var cache = new MessageIdDedupeCache(16);
        var act = () => cache.StoreResult(messageId!, Result("x"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StoreResult_NullResult_Throws()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1");
        var act = () => cache.StoreResult("m1", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ===== Evict un-poison =====

    [Fact]
    public void Evict_AfterTryAdd_MakesNextTryAddAMiss()
    {
        // The invariant: a processing error after a successful TryAdd evicts the key so the
        // broker redelivery re-processes cleanly (MISS again) instead of a resultless HIT silently
        // dropping the job.
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1").Should().BeTrue();
        cache.Evict("m1");

        cache.TryAdd("m1").Should().BeTrue("after Evict the key is un-poisoned → redelivery re-runs the job");
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void Evict_RemovesStoredResultToo()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("m1");
        cache.StoreResult("m1", Result("exec-1"));
        cache.Evict("m1");

        cache.TryGetResult("m1", out var fetched).Should().BeFalse(
            "Evict drops the result entry as well as the LRU index entry");
        fetched.Should().BeNull();
    }

    [Fact]
    public void Evict_DecrementsCount()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("a");
        cache.TryAdd("b");
        cache.Evict("a");
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void Evict_NeverSeenId_IsNoOp()
    {
        var cache = new MessageIdDedupeCache(16);
        cache.TryAdd("a");
        var act = () => cache.Evict("ghost");
        act.Should().NotThrow();
        cache.Count.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Evict_NullOrEmptyMessageId_Throws(string? messageId)
    {
        var cache = new MessageIdDedupeCache(16);
        var act = () => cache.Evict(messageId!);
        act.Should().Throw<ArgumentException>();
    }

    // ===== LRU eviction + results coupling =====

    [Fact]
    public void TryAdd_BeyondCapacity_EvictsLruEntry()
    {
        var cache = new MessageIdDedupeCache(capacity: 3);
        cache.TryAdd("a");
        cache.TryAdd("b");
        cache.TryAdd("c");
        cache.TryAdd("d"); // overflow → evicts LRU "a"

        cache.Count.Should().Be(3);
        cache.TryAdd("a").Should().BeTrue("'a' was the LRU and got evicted → it is a MISS again");
        cache.Count.Should().Be(3);
    }

    [Fact]
    public void TryAdd_BeyondCapacity_AlsoEvictsStoredResultOfLruKey()
    {
        // the _results dictionary must be evicted in lock-step with the LRU index so an
        // old MessageId's cached result does not linger unbounded after its index entry is gone.
        var cache = new MessageIdDedupeCache(capacity: 3);
        cache.TryAdd("a");
        cache.StoreResult("a", Result("exec-a"));
        cache.TryAdd("b");
        cache.TryAdd("c");

        cache.TryGetResult("a", out _).Should().BeTrue("'a' is still in-cache before overflow");

        cache.TryAdd("d"); // overflow → "a" evicted from BOTH index and results

        cache.TryGetResult("a", out var fetched).Should().BeFalse(
 "the LRU eviction also dropped the coupled result entry → bounded");
        fetched.Should().BeNull();
    }

    [Fact]
    public void TryAdd_HitOnExistingKey_TouchesMru_DelaysEviction()
    {
        // MRU touch: re-seeing "a" moves it to the head so it survives the next overflow; "b" (now LRU)
        // is evicted instead.
        var cache = new MessageIdDedupeCache(capacity: 3);
        cache.TryAdd("a");
        cache.TryAdd("b");
        cache.TryAdd("c");
        cache.TryAdd("a").Should().BeFalse("re-seeing 'a' is a HIT and moves it to MRU");

        cache.TryAdd("d"); // overflow → evicts LRU which is now "b", NOT "a"

        cache.TryAdd("a").Should().BeFalse("'a' was touched to MRU and survived the overflow");
        cache.TryAdd("b").Should().BeTrue("'b' became the LRU and was evicted");
    }

    [Fact]
    public void TryAdd_ManyDistinctIds_CountNeverExceedsCapacity()
    {
        var cache = new MessageIdDedupeCache(capacity: 50);
        for (var i = 0; i < 500; i++)
        {
            cache.TryAdd($"id-{i}");
            cache.StoreResult($"id-{i}", Result($"exec-{i}"));
        }

        cache.Count.Should().Be(50, "the LRU is hard-bounded at capacity");
    }

    [Fact]
    public void TryAdd_ManyDistinctIds_ResultsAlsoBounded()
    {
        // Stress the coupling: after far more than capacity insertions, very old results must be gone.
        var cache = new MessageIdDedupeCache(capacity: 50);
        for (var i = 0; i < 500; i++)
        {
            cache.TryAdd($"id-{i}");
            cache.StoreResult($"id-{i}", Result($"exec-{i}"));
        }

        cache.TryGetResult("id-0", out _).Should().BeFalse("the earliest key's result was evicted with its index");
        cache.TryGetResult("id-499", out var recent).Should().BeTrue("the most recent key's result survives");
        recent!.ExecutionId.Should().Be("exec-499");
    }

    // ===== Concurrency smoke (lock correctness) =====

    [Fact]
    public async Task TryAdd_ConcurrentDistinctIds_NoCorruption()
    {
        var cache = new MessageIdDedupeCache(capacity: 2048);
        var tasks = Enumerable.Range(0, 16).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                cache.TryAdd($"t{t}-i{i}");
            }
        }));
        await Task.WhenAll(tasks);

        cache.Count.Should().Be(16 * 100, "all distinct ids were added without lost updates");
    }

    [Fact]
    public async Task TryAdd_ConcurrentSameId_ExactlyOneMiss()
    {
        var cache = new MessageIdDedupeCache(capacity: 64);
        var misses = 0;
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (cache.TryAdd("contended"))
            {
                Interlocked.Increment(ref misses);
            }
        }));
        await Task.WhenAll(tasks);

        misses.Should().Be(1, "exactly one concurrent caller wins the MISS; the rest are HITs");
    }
}
