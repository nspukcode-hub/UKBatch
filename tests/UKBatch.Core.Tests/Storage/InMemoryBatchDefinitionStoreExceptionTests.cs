using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// throw-site tests for
/// <see cref="BatchDefinitionDuplicateNameException"/>, <see cref="BatchDefinitionNotFoundException"/>,
/// <see cref="BatchConcurrencyConflictException"/> in <see cref="InMemoryBatchDefinitionStore"/>.
/// </summary>
public class InMemoryBatchDefinitionStoreExceptionTests
{
    private static BatchDefinition NewDef(string id, string name, BatchSource source = BatchSource.Dashboard, int version = 0) => new()
    {
        Id = id,
        Name = name,
        Source = source,
        Steps = Array.Empty<BatchStep>(),
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        OnFailureSteps = Array.Empty<BatchStep>(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = version,
    };

    [Fact]
    public async Task CreateAsync_DuplicateName_ThrowsBatchDefinitionDuplicateName()
    {
        var store = new InMemoryBatchDefinitionStore();
        await store.CreateAsync(NewDef("id-1", "myBatch"), default).ConfigureAwait(false);

        var act = async () => await store.CreateAsync(NewDef("id-2", "myBatch"), default);
        var ex = await act.Should().ThrowAsync<BatchDefinitionDuplicateNameException>().ConfigureAwait(false);
        ex.Which.Name.Should().Be("myBatch");
        ex.Which.BatchSource.Should().Be(BatchSource.Dashboard);
        ex.Which.Should().BeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_DefinitionMissing_ThrowsBatchDefinitionNotFound()
    {
        var store = new InMemoryBatchDefinitionStore();

        var act = async () => await store.UpdateAsync(NewDef("missing-id", "x", version: 1), default);
        var ex = await act.Should().ThrowAsync<BatchDefinitionNotFoundException>().ConfigureAwait(false);
        ex.Which.BatchDefinitionId.Should().Be("missing-id");
        ex.Which.Should().BeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_ConcurrencyConflict_ThrowsBatchConcurrencyConflict()
    {
        var store = new InMemoryBatchDefinitionStore();
        var created = await store.CreateAsync(NewDef("id-cc", "myBatch"), default).ConfigureAwait(false);
        // Store version is now 1; submit version 99 to trigger optimistic concurrency conflict.
        var stale = created with { Version = 99 };

        var act = async () => await store.UpdateAsync(stale, default);
        var ex = await act.Should().ThrowAsync<BatchConcurrencyConflictException>().ConfigureAwait(false);
        ex.Which.BatchDefinitionId.Should().Be("id-cc");
        ex.Which.StoreVersion.Should().Be(1);
        ex.Which.CallerVersion.Should().Be(99);
        ex.Which.Should().BeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_RenameToExisting_ThrowsBatchDefinitionDuplicateName()
    {
        var store = new InMemoryBatchDefinitionStore();
        var def1 = await store.CreateAsync(NewDef("id-1", "name-A"), default).ConfigureAwait(false);
        await store.CreateAsync(NewDef("id-2", "name-B"), default).ConfigureAwait(false);

        // Try to rename def1 to "name-B" — should throw duplicate name (NOT concurrency conflict).
        var renamed = def1 with { Name = "name-B" };

        var act = async () => await store.UpdateAsync(renamed, default);
        var ex = await act.Should().ThrowAsync<BatchDefinitionDuplicateNameException>().ConfigureAwait(false);
        ex.Which.Name.Should().Be("name-B");
        ex.Which.BatchSource.Should().Be(BatchSource.Dashboard);
    }
}
