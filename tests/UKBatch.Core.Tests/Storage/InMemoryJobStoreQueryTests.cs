using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Models;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// <see cref="InMemoryJobStore.QueryAsync"/> filters on the new
/// <c>JobQuery.BatchDefinitionId</c> field.
/// </summary>
public class InMemoryJobStoreQueryTests
{
    private static InMemoryJobStore CreateStore() =>
        new(TimeProvider.System, Options.Create(new UKBatchOptions()), new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));

    private static async Task InsertAsync(InMemoryJobStore store, string execId, string? batchDefId, JobStatus status = JobStatus.Pending)
    {
        var execution = new JobExecution
        {
            ExecutionId = execId,
            JobName = "Test.Job",
            BatchId = batchDefId is null ? null : $"run-{execId}",
            BatchStepId = batchDefId is null ? null : "step-1",
            BatchDefinitionId = batchDefId,
            Status = status,
            Parameters = new Dictionary<string, object?>(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = 1,
            MaxRetries = 0,
            Processed = 0,
            Failed = 0,
        };
        // InsertAsync is now a public IJobStoreInternal member (was internal).
        await store.InsertAsync(execution, CancellationToken.None).ConfigureAwait(false);
    }

    [Fact]
    public async Task QueryAsync_FiltersByBatchDefinitionId_HappyPath()
    {
        var store = CreateStore();
        await InsertAsync(store, "e1", "def-A");
        await InsertAsync(store, "e2", "def-A");
        await InsertAsync(store, "e3", "def-B");

        var result = await store.QueryAsync(new JobQuery { BatchDefinitionId = "def-A", Limit = 100 }, CancellationToken.None);
        result.Should().HaveCount(2);
        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "e1", "e2" });
    }

    [Fact]
    public async Task QueryAsync_FiltersByBatchDefinitionId_NoMatch()
    {
        var store = CreateStore();
        await InsertAsync(store, "e1", "def-A");
        await InsertAsync(store, "e2", "def-A");

        var result = await store.QueryAsync(new JobQuery { BatchDefinitionId = "def-MISSING", Limit = 100 }, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_BatchDefinitionId_NullFilter_NoFilter()
    {
        var store = CreateStore();
        await InsertAsync(store, "e1", "def-A");
        await InsertAsync(store, "e2", "def-B");

        var result = await store.QueryAsync(new JobQuery { BatchDefinitionId = null, Limit = 100 }, CancellationToken.None);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_BatchDefinitionId_EmptyStringFilter_NoFilter()
    {
        var store = CreateStore();
        await InsertAsync(store, "e1", "def-A");
        await InsertAsync(store, "e2", "def-B");

        var result = await store.QueryAsync(new JobQuery { BatchDefinitionId = "", Limit = 100 }, CancellationToken.None);
        result.Should().HaveCount(2, "empty string is treated as 'no filter applied' at the adapter layer.");
    }

    [Fact]
    public async Task QueryAsync_BatchDefinitionId_StandaloneJob_NotMatched()
    {
        var store = CreateStore();
        // Standalone job: BatchDefinitionId is null.
        await InsertAsync(store, "stand1", batchDefId: null);
        // Batch-spawned: BatchDefinitionId set.
        await InsertAsync(store, "batch1", "def-A");

        var result = await store.QueryAsync(new JobQuery { BatchDefinitionId = "def-A", Limit = 100 }, CancellationToken.None);
        result.Should().HaveCount(1);
        result.Single().ExecutionId.Should().Be("batch1");
    }

    [Fact]
    public async Task QueryAsync_CombinesBatchDefinitionId_WithOtherFilters()
    {
        var store = CreateStore();
        await InsertAsync(store, "e1", "def-A", JobStatus.Completed);
        await InsertAsync(store, "e2", "def-A", JobStatus.Failed);
        await InsertAsync(store, "e3", "def-B", JobStatus.Completed);

        var result = await store.QueryAsync(new JobQuery
        {
            BatchDefinitionId = "def-A",
            Statuses = new[] { JobStatus.Failed },
            Limit = 100,
        }, CancellationToken.None);
        result.Should().HaveCount(1);
        result.Single().ExecutionId.Should().Be("e2");
    }
}
