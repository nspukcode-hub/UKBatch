using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Models;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// <see cref="EfJobStore.QueryAsync"/> / <see cref="EfJobStore.CountAsync"/> filter + paging + sort
/// behavior on SQLite. Mirrors the InMemory query semantics (the parity harness asserts they match
/// exactly).
/// </summary>
public sealed class EfJobStoreQueryTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfJobStore _store = default!;

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _store = new EfJobStore(_harness.Factory, new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance),
            _harness.Clock, NullLogger<EfJobStore>.Instance);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private async Task SeedAsync(params JobExecution[] executions)
    {
        foreach (var e in executions)
        {
            await _store.InsertAsync(e, CancellationToken.None);
        }
    }

    [Fact]
    public async Task QueryAsync_FilterByStatuses_ReturnsOnlyMatching()
    {
        await SeedAsync(
            TestData.Execution("e1", status: JobStatus.Completed),
            TestData.Execution("e2", status: JobStatus.Failed),
            TestData.Execution("e3", status: JobStatus.Pending));

        var result = await _store.QueryAsync(new JobQuery { Statuses = new[] { JobStatus.Completed, JobStatus.Failed }, Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "e1", "e2" });
    }

    [Fact]
    public async Task QueryAsync_EmptyStatuses_NoFilter()
    {
        await SeedAsync(TestData.Execution("e1", status: JobStatus.Completed), TestData.Execution("e2", status: JobStatus.Pending));
        var result = await _store.QueryAsync(new JobQuery { Statuses = Array.Empty<JobStatus>(), Limit = 100 }, CancellationToken.None);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_FilterByJobName()
    {
        await SeedAsync(
            TestData.Execution("e1", jobName: "alpha"),
            TestData.Execution("e2", jobName: "beta"),
            TestData.Execution("e3", jobName: "alpha"));

        var result = await _store.QueryAsync(new JobQuery { JobName = "alpha", Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "e1", "e3" });
    }

    [Fact]
    public async Task QueryAsync_FilterByBatchId()
    {
        await SeedAsync(
            TestData.Execution("e1", batchId: "run-A"),
            TestData.Execution("e2", batchId: "run-B"));

        var result = await _store.QueryAsync(new JobQuery { BatchId = "run-A", Limit = 100 }, CancellationToken.None);
        result.Should().ContainSingle().Which.ExecutionId.Should().Be("e1");
    }

    [Fact]
    public async Task QueryAsync_FilterByBatchDefinitionId()
    {
        await SeedAsync(
            TestData.Execution("e1", batchDefinitionId: "def-A"),
            TestData.Execution("e2", batchDefinitionId: "def-A"),
            TestData.Execution("e3", batchDefinitionId: "def-B"));

        var result = await _store.QueryAsync(new JobQuery { BatchDefinitionId = "def-A", Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "e1", "e2" });
    }

    [Fact]
    public async Task QueryAsync_FilterByBatchDefinitionId_StandaloneJobNeverMatches()
    {
        await SeedAsync(
            TestData.Execution("standalone", batchDefinitionId: null),
            TestData.Execution("batched", batchDefinitionId: "def-A"));

        var result = await _store.QueryAsync(new JobQuery { BatchDefinitionId = "def-A", Limit = 100 }, CancellationToken.None);
        result.Should().ContainSingle().Which.ExecutionId.Should().Be("batched");
    }

    [Fact]
    public async Task QueryAsync_FilterByWorkerName()
    {
        await SeedAsync(
            TestData.Execution("e1", workerName: "worker-1"),
            TestData.Execution("e2", workerName: "worker-2"));

        var result = await _store.QueryAsync(new JobQuery { WorkerName = "worker-1", Limit = 100 }, CancellationToken.None);
        result.Should().ContainSingle().Which.ExecutionId.Should().Be("e1");
    }

    [Fact]
    public async Task QueryAsync_FilterByFromUtc_InclusiveLowerBound()
    {
        await SeedAsync(
            TestData.Execution("early", enqueuedAtUtc: T0),
            TestData.Execution("onbound", enqueuedAtUtc: T0.AddHours(1)),
            TestData.Execution("late", enqueuedAtUtc: T0.AddHours(2)));

        var result = await _store.QueryAsync(new JobQuery { FromUtc = T0.AddHours(1), Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "onbound", "late" }, "FromUtc is inclusive (>=)");
    }

    [Fact]
    public async Task QueryAsync_FilterByToUtc_ExclusiveUpperBound()
    {
        await SeedAsync(
            TestData.Execution("early", enqueuedAtUtc: T0),
            TestData.Execution("onbound", enqueuedAtUtc: T0.AddHours(1)),
            TestData.Execution("late", enqueuedAtUtc: T0.AddHours(2)));

        var result = await _store.QueryAsync(new JobQuery { ToUtc = T0.AddHours(1), Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "early" }, "ToUtc is exclusive (<)");
    }

    [Fact]
    public async Task QueryAsync_FilterBySearchText_MatchesJobNameOrLastError()
    {
        await SeedAsync(
            TestData.Execution("e1", jobName: "InvoiceProcessing"),
            TestData.Execution("e2", jobName: "other", lastError: "Invoice failed validation"),
            TestData.Execution("e3", jobName: "unrelated"));

        var result = await _store.QueryAsync(new JobQuery { SearchText = "invoice", Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "e1", "e2" }, "case-insensitive substring across JobName + LastError");
    }

    [Fact]
    public async Task QueryAsync_SearchText_LiteralPercent_MatchesLiterally()
    {
        // a literal % in the needle must NOT act as a wildcard.
        await SeedAsync(
            TestData.Execution("pct", jobName: "100%done"),
            TestData.Execution("nopct", jobName: "100done"));

        var result = await _store.QueryAsync(new JobQuery { SearchText = "100%done", Limit = 100 }, CancellationToken.None);
        result.Should().ContainSingle().Which.ExecutionId.Should().Be("pct");
    }

    [Fact]
    public async Task QueryAsync_SearchText_LiteralUnderscore_MatchesLiterally()
    {
        await SeedAsync(
            TestData.Execution("us", jobName: "a_b"),
            TestData.Execution("nous", jobName: "axb"));

        var result = await _store.QueryAsync(new JobQuery { SearchText = "a_b", Limit = 100 }, CancellationToken.None);
        result.Should().ContainSingle().Which.ExecutionId.Should().Be("us", "the underscore is escaped, so it does not match any single char");
    }

    [Fact]
    public async Task QueryAsync_EmptyStringFilters_AreNoOps()
    {
        await SeedAsync(TestData.Execution("e1", jobName: "a"), TestData.Execution("e2", jobName: "b"));

        var result = await _store.QueryAsync(
            new JobQuery { JobName = "", BatchId = "", BatchDefinitionId = "", WorkerName = "", SearchText = "", Limit = 100 },
            CancellationToken.None);
        result.Should().HaveCount(2, "empty-string filters are treated as no-filter (mirrors InMemory)");
    }

    [Fact]
    public async Task QueryAsync_Paging_OffsetAndLimit()
    {
        for (var i = 0; i < 10; i++)
        {
            await _store.InsertAsync(TestData.Execution($"e{i:D2}", enqueuedAtUtc: T0.AddMinutes(i)), CancellationToken.None);
        }

        // Ascending by enqueued time: e00..e09. Offset 2, limit 3 => e02,e03,e04.
        var page = await _store.QueryAsync(new JobQuery { Offset = 2, Limit = 3, DescendingByEnqueuedAt = false }, CancellationToken.None);
        page.Select(e => e.ExecutionId).Should().Equal("e02", "e03", "e04");
    }

    [Fact]
    public async Task QueryAsync_DescendingByEnqueuedAt_NewestFirst()
    {
        await SeedAsync(
            TestData.Execution("old", enqueuedAtUtc: T0),
            TestData.Execution("mid", enqueuedAtUtc: T0.AddHours(1)),
            TestData.Execution("new", enqueuedAtUtc: T0.AddHours(2)));

        var result = await _store.QueryAsync(new JobQuery { DescendingByEnqueuedAt = true, Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().Equal("new", "mid", "old");
    }

    [Fact]
    public async Task QueryAsync_SortStability_ExecutionIdTiebreak_OnEqualTimestamps()
    {
        // Three executions at the SAME timestamp — tie broken by ExecutionId ascending (mirrors InMemory).
        await SeedAsync(
            TestData.Execution("c", enqueuedAtUtc: T0),
            TestData.Execution("a", enqueuedAtUtc: T0),
            TestData.Execution("b", enqueuedAtUtc: T0));

        var asc = await _store.QueryAsync(new JobQuery { DescendingByEnqueuedAt = false, Limit = 100 }, CancellationToken.None);
        asc.Select(e => e.ExecutionId).Should().Equal(new[] { "a", "b", "c" });   // ExecutionId ascending is the stable tiebreak

        var desc = await _store.QueryAsync(new JobQuery { DescendingByEnqueuedAt = true, Limit = 100 }, CancellationToken.None);
        // tiebreak stays ExecutionId-ascending even when time is descending (mirrors InMemory ThenBy(ExecutionId)).
        desc.Select(e => e.ExecutionId).Should().Equal(new[] { "a", "b", "c" });
    }

    [Fact]
    public async Task CountAsync_RespectsFilter_IgnoresPaging()
    {
        await SeedAsync(
            TestData.Execution("e1", status: JobStatus.Completed),
            TestData.Execution("e2", status: JobStatus.Completed),
            TestData.Execution("e3", status: JobStatus.Failed));

        var count = await _store.CountAsync(new JobQuery { Statuses = new[] { JobStatus.Completed }, Offset = 1, Limit = 1 }, CancellationToken.None);
        count.Should().Be(2, "Count applies the filter but ignores Offset/Limit");
    }

    [Fact]
    public async Task QueryAsync_CombinesMultipleFilters()
    {
        await SeedAsync(
            TestData.Execution("e1", jobName: "j", batchDefinitionId: "def-A", status: JobStatus.Failed),
            TestData.Execution("e2", jobName: "j", batchDefinitionId: "def-A", status: JobStatus.Completed),
            TestData.Execution("e3", jobName: "j", batchDefinitionId: "def-B", status: JobStatus.Failed));

        var result = await _store.QueryAsync(new JobQuery
        {
            JobName = "j",
            BatchDefinitionId = "def-A",
            Statuses = new[] { JobStatus.Failed },
            Limit = 100,
        }, CancellationToken.None);
        result.Should().ContainSingle().Which.ExecutionId.Should().Be("e1");
    }

    [Fact]
    public async Task QueryAsync_EmptyStore_ReturnsEmpty()
    {
        var result = await _store.QueryAsync(new JobQuery { Limit = 100 }, CancellationToken.None);
        result.Should().BeEmpty();
    }
}
