using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Transport.Http.Endpoints;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Endpoints;

/// <summary>
/// MessageId dedupe cache for receiver-side idempotency.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class MessageIdDedupeCacheTests
{
    [Fact]
    public void TryAdd_NewId_StoresResult()
    {
        var cache = new MessageIdDedupeCache(capacity: 64);
        cache.TryAdd("msg-1").Should().BeTrue();
        var result = new JobResult
        {
            ExecutionId = "exec-1",
            Status = JobStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        cache.StoreResult("msg-1", result);
        cache.TryGetResult("msg-1", out var fetched).Should().BeTrue();
        fetched!.ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public void TryAdd_DuplicateId_ReturnsFalse_AndCachedResultReplayable()
    {
        var cache = new MessageIdDedupeCache(capacity: 64);
        cache.TryAdd("msg-X").Should().BeTrue();
        var result = new JobResult
        {
            ExecutionId = "exec-X",
            Status = JobStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        cache.StoreResult("msg-X", result);
        cache.TryAdd("msg-X").Should().BeFalse();
        cache.TryGetResult("msg-X", out var replay).Should().BeTrue();
        replay!.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void TryAdd_BeyondCapacity_EvictsOldest_ResultStoreStaysBounded()
    {
        // The result store is evicted in lock-step with the LRU index, so neither structure grows
        // without limit once capacity is exceeded.
        var cache = new MessageIdDedupeCache(capacity: 4);
        JobResult Make(string id) => new()
        {
            ExecutionId = id,
            Status = JobStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        for (var i = 0; i < 4; i++)
        {
            cache.TryAdd($"msg-{i}").Should().BeTrue();
            cache.StoreResult($"msg-{i}", Make($"exec-{i}"));
        }
        cache.Count.Should().Be(4);
        cache.TryGetResult("msg-0", out _).Should().BeTrue();

        // Overflow: msg-0 is the LRU tail and must be dropped from BOTH the index and the result store.
        cache.TryAdd("msg-4").Should().BeTrue();
        cache.StoreResult("msg-4", Make("exec-4"));

        cache.Count.Should().Be(4);
        cache.TryGetResult("msg-0", out _).Should().BeFalse();
        cache.TryGetResult("msg-4", out var newest).Should().BeTrue();
        newest!.ExecutionId.Should().Be("exec-4");
    }
}
