using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// The in-memory run store is the reference implementation the EF adapter must match. These pin its
/// contract: create/get round-trip, the no-op-on-absent <c>CompleteAsync</c> (a completion that lost its
/// create must NOT resurrect a half-populated row), the filter semantics (definition id + status set +
/// the <c>IncludeRunning</c> rule for null-status runs), stable newest-first ordering with an id tiebreak,
/// and a count that ignores paging.
/// </summary>
public class InMemoryBatchRunStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BatchRun Run(
        string batchId,
        string batchDefinitionId = "def-1",
        string batchName = "pipeline",
        JobStatus? status = null,
        DateTimeOffset? startedAtUtc = null,
        int stepCount = 1) => new()
    {
        BatchId = batchId,
        BatchDefinitionId = batchDefinitionId,
        BatchName = batchName,
        Status = status,
        TriggeredBy = "tester",
        StartedAtUtc = startedAtUtc ?? T0,
        CompletedAtUtc = null,
        StepCount = stepCount,
        Total = 0,
        Succeeded = 0,
        Failed = 0,
        Cancelled = 0,
    };

    [Fact]
    public async Task CreateThenGet_RoundTripsEveryField()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("r1", batchDefinitionId: "def-A", batchName: "Invoices", stepCount: 5), CancellationToken.None);

        var fetched = (await store.GetAsync("r1", CancellationToken.None))!;

        fetched.BatchId.Should().Be("r1");
        fetched.BatchDefinitionId.Should().Be("def-A");
        fetched.BatchName.Should().Be("Invoices");
        fetched.Status.Should().BeNull("a freshly created run is in-progress");
        fetched.TriggeredBy.Should().Be("tester");
        fetched.StartedAtUtc.Should().Be(T0);
        fetched.CompletedAtUtc.Should().BeNull();
        fetched.StepCount.Should().Be(5);
        fetched.Total.Should().Be(0);
        fetched.Succeeded.Should().Be(0);
        fetched.Failed.Should().Be(0);
        fetched.Cancelled.Should().Be(0);
    }

    [Fact]
    public async Task Get_MissingId_ReturnsNull()
    {
        var store = new InMemoryBatchRunStore();
        (await store.GetAsync("nope", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DuplicateId_ThrowsInvalidOperation()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("dup"), CancellationToken.None);

        var act = async () => await store.CreateAsync(Run("dup"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task CompleteAsync_ExistingRun_StampsStatusCountsAndCompletedAt()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("r1", stepCount: 3), CancellationToken.None);

        await store.CompleteAsync("r1", JobStatus.Completed, new BatchRunCounts(3, 3, 0, 0), T0.AddMinutes(5), CancellationToken.None);

        var fetched = (await store.GetAsync("r1", CancellationToken.None))!;
        fetched.Status.Should().Be(JobStatus.Completed);
        fetched.Total.Should().Be(3);
        fetched.Succeeded.Should().Be(3);
        fetched.Failed.Should().Be(0);
        fetched.Cancelled.Should().Be(0);
        fetched.CompletedAtUtc.Should().Be(T0.AddMinutes(5));
        fetched.StepCount.Should().Be(3, "completion must not overwrite the create-time topology count");
    }

    [Fact]
    public async Task CompleteAsync_AbsentId_IsNoOp_DoesNotInsert()
    {
        // The whole reason for the compare-and-swap loop: a completion whose create write was lost must
        // not resurrect a half-populated run (no StepCount / StartedAt). Absent id → silent no-op.
        var store = new InMemoryBatchRunStore();

        var act = async () => await store.CompleteAsync(
            "ghost", JobStatus.Failed, new BatchRunCounts(1, 0, 1, 0), T0, CancellationToken.None);

        await act.Should().NotThrowAsync("completion on a missing row must never crash the runtime finally");
        (await store.GetAsync("ghost", CancellationToken.None)).Should().BeNull("the absent id must not be inserted");
    }

    [Fact]
    public async Task CompleteAsync_CalledTwice_IsLastWriteWins()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("r1"), CancellationToken.None);

        await store.CompleteAsync("r1", JobStatus.Completed, new BatchRunCounts(1, 1, 0, 0), T0.AddMinutes(1), CancellationToken.None);
        await store.CompleteAsync("r1", JobStatus.Failed, new BatchRunCounts(2, 1, 1, 0), T0.AddMinutes(2), CancellationToken.None);

        var fetched = (await store.GetAsync("r1", CancellationToken.None))!;
        fetched.Status.Should().Be(JobStatus.Failed, "a second completion overwrites — last write wins");
        fetched.Total.Should().Be(2);
        fetched.CompletedAtUtc.Should().Be(T0.AddMinutes(2));
    }

    [Fact]
    public async Task QueryAsync_FilterByBatchDefinitionId_ReturnsOnlyMatchingDefinition()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("a1", batchDefinitionId: "def-A"), CancellationToken.None);
        await store.CreateAsync(Run("a2", batchDefinitionId: "def-A"), CancellationToken.None);
        await store.CreateAsync(Run("b1", batchDefinitionId: "def-B"), CancellationToken.None);

        var result = await store.QueryAsync(new BatchRunQuery { BatchDefinitionId = "def-A", Limit = 100 }, CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "a1", "a2" });
    }

    [Fact]
    public async Task QueryAsync_IncludeRunningTrue_ReturnsRunningRuns()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("running", status: null), CancellationToken.None);
        await store.CreateAsync(Run("done", status: JobStatus.Completed), CancellationToken.None);

        var result = await store.QueryAsync(new BatchRunQuery { IncludeRunning = true, Limit = 100 }, CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "running", "done" },
            "IncludeRunning=true with no status filter returns every run");
    }

    [Fact]
    public async Task QueryAsync_IncludeRunningFalse_ExcludesRunningRuns()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("running", status: null), CancellationToken.None);
        await store.CreateAsync(Run("done", status: JobStatus.Completed), CancellationToken.None);

        var result = await store.QueryAsync(new BatchRunQuery { IncludeRunning = false, Limit = 100 }, CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "done" },
            "IncludeRunning=false drops null-status (in-progress) runs");
    }

    [Fact]
    public async Task QueryAsync_StatusesFilter_WithIncludeRunningFalse_ReturnsOnlyMatchingTerminalRuns()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("failed", status: JobStatus.Failed), CancellationToken.None);
        await store.CreateAsync(Run("completed", status: JobStatus.Completed), CancellationToken.None);
        await store.CreateAsync(Run("running", status: null), CancellationToken.None);

        var result = await store.QueryAsync(
            new BatchRunQuery { Statuses = new[] { JobStatus.Failed }, IncludeRunning = false, Limit = 100 },
            CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "failed" },
            "a status filter returns only the matching terminal runs; a null-status run cannot be in any set");
    }

    [Fact]
    public async Task QueryAsync_DescendingByStartedAt_NewestFirst_WithIdTiebreak()
    {
        var store = new InMemoryBatchRunStore();
        // Two equal timestamps + one later: time desc first, then id desc as the tiebreak.
        await store.CreateAsync(Run("a", startedAtUtc: T0), CancellationToken.None);
        await store.CreateAsync(Run("b", startedAtUtc: T0), CancellationToken.None);
        await store.CreateAsync(Run("late", startedAtUtc: T0.AddHours(1)), CancellationToken.None);

        var result = await store.QueryAsync(new BatchRunQuery { DescendingByStartedAt = true, Limit = 100 }, CancellationToken.None);

        result.Select(r => r.BatchId).Should().Equal(
            new[] { "late", "b", "a" }, "newest first, then id descending on equal timestamps");
    }

    [Fact]
    public async Task QueryAsync_OffsetAndLimit_PagesResults()
    {
        var store = new InMemoryBatchRunStore();
        for (var i = 0; i < 10; i++)
        {
            await store.CreateAsync(Run($"r{i:D2}", startedAtUtc: T0.AddMinutes(i)), CancellationToken.None);
        }

        var page = await store.QueryAsync(
            new BatchRunQuery { Offset = 2, Limit = 3, DescendingByStartedAt = false }, CancellationToken.None);

        page.Select(r => r.BatchId).Should().Equal("r02", "r03", "r04");
    }

    [Fact]
    public async Task CountAsync_RespectsFilter_IgnoresPaging()
    {
        var store = new InMemoryBatchRunStore();
        await store.CreateAsync(Run("a1", batchDefinitionId: "def-A"), CancellationToken.None);
        await store.CreateAsync(Run("a2", batchDefinitionId: "def-A"), CancellationToken.None);
        await store.CreateAsync(Run("b1", batchDefinitionId: "def-B"), CancellationToken.None);

        var count = await store.CountAsync(
            new BatchRunQuery { BatchDefinitionId = "def-A", Offset = 1, Limit = 1 }, CancellationToken.None);

        count.Should().Be(2, "Count applies the filter but ignores Offset/Limit");
    }
}
