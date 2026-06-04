using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// <see cref="InMemoryJobStore"/> round-trips
/// <see cref="JobExecution.BatchDefinitionId"/> on the <c>InsertAsync</c> path; standalone jobs
/// via <c>CreateAsync(JobDefinition)</c> leave it null.
/// </summary>
public class InMemoryJobStoreBatchDefinitionIdTests
{
    private static InMemoryJobStore CreateStore() =>
        new(TimeProvider.System, Options.Create(new UKBatchOptions()), new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));

    [Fact]
    public async Task InsertAsync_PreservesBatchDefinitionId()
    {
        var store = CreateStore();
        var execution = new JobExecution
        {
            ExecutionId = "exec-1",
            JobName = "Test.Job",
            BatchId = "batch-run-1",
            BatchStepId = "step-1",
            BatchDefinitionId = "batch-def-xyz",
            Status = JobStatus.Pending,
            Parameters = new Dictionary<string, object?>(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = 1,
            MaxRetries = 0,
            Processed = 0,
            Failed = 0,
        };

        // InsertAsync is now a public IJobStoreInternal member (was internal).
        var inserted = await store.InsertAsync(execution, CancellationToken.None).ConfigureAwait(false);

        inserted.BatchDefinitionId.Should().Be("batch-def-xyz");

        // Round-trip via GetAsync.
        var fetched = await store.GetAsync("exec-1", default).ConfigureAwait(false);
        fetched.Should().NotBeNull();
        fetched!.BatchDefinitionId.Should().Be("batch-def-xyz");
    }

    [Fact]
    public async Task CreateAsync_StandaloneJob_BatchDefinitionId_IsNull()
    {
        var store = CreateStore();
        var def = new JobDefinition
        {
            Name = "Test.Standalone",
            IsPartitioned = false,
            MaxRetries = 0,
            TimeoutSeconds = 0,
            DefaultParameters = new Dictionary<string, object?>(),
            Tags = Array.Empty<string>(),
        };
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);
        execution.BatchDefinitionId.Should().BeNull("standalone job has no batch definition.");
        execution.BatchId.Should().BeNull();
    }
}
