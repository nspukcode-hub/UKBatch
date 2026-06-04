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
}
