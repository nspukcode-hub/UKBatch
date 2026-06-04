using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// <see cref="IJobStoreInternal.InsertAsync"/> contract: round-trips the pre-assigned id AND
/// <see cref="JobExecution.BatchDefinitionId"/> (the headline correctness item — an adapter
/// implementing this interface eliminates the JobRunner fallback-warning path); duplicate-id rejects.
///  (BatchDefinitionId is the round-trip the whole adapter exists to carry).
/// </summary>
public sealed class EfJobStoreInsertContractTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfJobStore _store = default!;

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _store = new EfJobStore(_harness.Factory, new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance),
            _harness.Clock, NullLogger<EfJobStore>.Instance);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task InsertAsync_PreservesPreAssignedExecutionId()
    {
        var exec = TestData.Execution("predefined-id-123");
        var returned = await _store.InsertAsync(exec, CancellationToken.None);

        returned.ExecutionId.Should().Be("predefined-id-123");
        var fetched = await _store.GetAsync("predefined-id-123", CancellationToken.None);
        fetched!.ExecutionId.Should().Be("predefined-id-123");
    }

    [Fact]
    public async Task InsertAsync_RoundTripsBatchDefinitionId_TheHeadlineCorrectnessItem()
    {
        var exec = TestData.Execution("e1", batchId: "run-77", batchStepId: "step-3", batchDefinitionId: "invoice-pipeline-def");
        await _store.InsertAsync(exec, CancellationToken.None);

        var fetched = await _store.GetAsync("e1", CancellationToken.None);
        fetched!.BatchDefinitionId.Should().Be("invoice-pipeline-def",
 "the EF adapter MUST round-trip BatchDefinitionId (contract) — it is the reason InsertAsync exists");
    }

    [Fact]
    public async Task InsertAsync_BatchDefinitionId_QueryableAfterInsert()
    {
        // The dashboard's "last N runs of this definition" relies on this being persisted + queryable.
        await _store.InsertAsync(TestData.Execution("e1", batchDefinitionId: "def-X"), CancellationToken.None);
        await _store.InsertAsync(TestData.Execution("e2", batchDefinitionId: "def-X"), CancellationToken.None);
        await _store.InsertAsync(TestData.Execution("e3", batchDefinitionId: "def-Y"), CancellationToken.None);

        var runs = await _store.QueryAsync(new JobQuery { BatchDefinitionId = "def-X", Limit = 100 }, CancellationToken.None);
        runs.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "e1", "e2" });
    }

    [Fact]
    public async Task InsertAsync_NullBatchDefinitionId_PersistsAsNull()
    {
        await _store.InsertAsync(TestData.Execution("standalone", batchDefinitionId: null), CancellationToken.None);
        var fetched = await _store.GetAsync("standalone", CancellationToken.None);
        fetched!.BatchDefinitionId.Should().BeNull();
    }

    [Fact]
    public async Task InsertAsync_DuplicateId_ThrowsInvalidOperation()
    {
        await _store.InsertAsync(TestData.Execution("dup"), CancellationToken.None);

        var act = async () => await _store.InsertAsync(TestData.Execution("dup"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task InsertAsync_NullExecution_Throws()
    {
        var act = async () => await _store.InsertAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Store_IsIJobStoreInternal_NoFallbackPath()
    {
        // The adapter implements IJobStoreInternal, so JobRunner dispatches InsertAsync directly (no
        // fallback to CreateAsync(JobDefinition) which would drop BatchDefinitionId — the guard).
        _store.Should().BeAssignableTo<IJobStoreInternal>();
        ((object)_store).Should().BeAssignableTo<IJobStore>();
    }
}
