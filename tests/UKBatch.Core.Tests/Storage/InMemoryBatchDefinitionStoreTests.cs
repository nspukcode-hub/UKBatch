using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// CRUD + optimistic concurrency tests for <see cref="InMemoryBatchDefinitionStore"/>.
/// </summary>
public class InMemoryBatchDefinitionStoreTests
{
    private static BatchDefinition NewDef(string id, BatchSource src = BatchSource.Dashboard) => new()
    {
        Id = id,
        Name = "batch-" + id,
        Source = src,
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
        Steps = new[]
        {
            new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
        },
    };

    [Fact]
    public async Task CreateAsync_NewDefinition_PersistsWithVersion1()
    {
        var store = new InMemoryBatchDefinitionStore();
        var def = NewDef("b1");
        var created = await store.CreateAsync(def, default).ConfigureAwait(false);
        created.Version.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_DuplicateId_Throws()
    {
        var store = new InMemoryBatchDefinitionStore();
        var def = NewDef("b1");
        await store.CreateAsync(def, default).ConfigureAwait(false);

        Func<Task> act = async () => await store.CreateAsync(def, default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*").ConfigureAwait(false);
    }

    [Fact]
    public async Task GetAsync_KnownId_ReturnsDefinition()
    {
        var store = new InMemoryBatchDefinitionStore();
        var def = NewDef("b1");
        await store.CreateAsync(def, default).ConfigureAwait(false);

        var fetched = await store.GetAsync("b1", default).ConfigureAwait(false);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be("b1");
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var store = new InMemoryBatchDefinitionStore();
        var fetched = await store.GetAsync("nonexistent", default).ConfigureAwait(false);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_MatchingVersion_BumpsVersion()
    {
        var store = new InMemoryBatchDefinitionStore();
        var def = NewDef("b1");
        var created = await store.CreateAsync(def, default).ConfigureAwait(false);

        var update = created with { Name = "renamed" };
        var updated = await store.UpdateAsync(update, default).ConfigureAwait(false);

        updated.Version.Should().Be(2);
        updated.Name.Should().Be("renamed");
    }

    [Fact]
    public async Task UpdateAsync_MismatchingVersion_ThrowsOptimisticConflict()
    {
        var store = new InMemoryBatchDefinitionStore();
        var def = NewDef("b1");
        var created = await store.CreateAsync(def, default).ConfigureAwait(false);

        var staleUpdate = created with { Version = 99, Name = "stale" };
        Func<Task> act = async () => await store.UpdateAsync(staleUpdate, default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*concurrency*").ConfigureAwait(false);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDefinition()
    {
        var store = new InMemoryBatchDefinitionStore();
        var def = NewDef("b1");
        await store.CreateAsync(def, default).ConfigureAwait(false);

        await store.DeleteAsync("b1", default).ConfigureAwait(false);

        (await store.GetAsync("b1", default).ConfigureAwait(false)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_DoesNotThrow()
    {
        var store = new InMemoryBatchDefinitionStore();
        Func<Task> act = async () => await store.DeleteAsync("nonexistent", default).ConfigureAwait(false);
        await act.Should().NotThrowAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task ListAsync_FilterBySource_ReturnsOnlyMatching()
    {
        var store = new InMemoryBatchDefinitionStore();
        await store.CreateAsync(NewDef("b1", BatchSource.Dashboard), default).ConfigureAwait(false);
        await store.CreateAsync(NewDef("b2", BatchSource.Api), default).ConfigureAwait(false);
        await store.CreateAsync(NewDef("b3", BatchSource.Dashboard), default).ConfigureAwait(false);

        var dashboard = await store.ListAsync(BatchSource.Dashboard, 0, 100, default).ConfigureAwait(false);
        dashboard.Select(d => d.Id).Should().BeEquivalentTo(new[] { "b1", "b3" });
    }

    [Fact]
    public async Task ListAsync_PagesByOffsetAndLimit()
    {
        var store = new InMemoryBatchDefinitionStore();
        for (var i = 0; i < 10; i++)
        {
            await store.CreateAsync(NewDef($"b{i:D2}"), default).ConfigureAwait(false);
        }

        var page1 = await store.ListAsync(BatchSource.Dashboard, 0, 3, default).ConfigureAwait(false);
        var page2 = await store.ListAsync(BatchSource.Dashboard, 3, 3, default).ConfigureAwait(false);

        page1.Should().HaveCount(3);
        page2.Should().HaveCount(3);
        page1.Select(d => d.Id).Should().NotIntersectWith(page2.Select(d => d.Id));
    }

    [Fact]
    public async Task CountAsync_ReturnsTotalForSource()
    {
        var store = new InMemoryBatchDefinitionStore();
        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync(NewDef($"b{i}", BatchSource.Api), default).ConfigureAwait(false);
        }
        await store.CreateAsync(NewDef("dash", BatchSource.Dashboard), default).ConfigureAwait(false);

        (await store.CountAsync(BatchSource.Api, default).ConfigureAwait(false)).Should().Be(5);
        (await store.CountAsync(BatchSource.Dashboard, default).ConfigureAwait(false)).Should().Be(1);
    }

    // ── BatchDefinition.Metadata round-trip ────────────

    [Fact]
    public async Task InMemoryStore_PreservesBatchDefinitionMetadata()
    {
        // InMemory store uses `definition with {... }`, so the new Metadata field rides along
        // verbatim — no code change, but this locks the contract (regression if a future refactor
        // ever rebuilds the record field-by-field and drops Metadata).
        var store = new InMemoryBatchDefinitionStore();
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dashboard.layoutHints"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["s1"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 100.0, ["y"] = 200.0 },
            },
        };
        var def = NewDef("meta-id") with { Metadata = metadata };
        await store.CreateAsync(def, default).ConfigureAwait(false);

        var fetched = await store.GetAsync("meta-id", default).ConfigureAwait(false);
        fetched!.Metadata.Should().BeSameAs(metadata, "InMemory keeps the dict reference verbatim");
        fetched.Metadata.Should().ContainKey("dashboard.layoutHints");
    }

    [Fact]
    public async Task InMemoryStore_UpdatePreservesNewMetadata()
    {
        // Simulate the drag-persist path: create, then UpdateAsync with NEW Metadata.
        var store = new InMemoryBatchDefinitionStore();
        var created = await store.CreateAsync(NewDef("drag-id"), default).ConfigureAwait(false);

        var newHints = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dashboard.layoutHints"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["s1"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 320.0, ["y"] = 80.0 },
            },
        };
        await store.UpdateAsync(created with { Metadata = newHints }, default).ConfigureAwait(false);

        var fetched = await store.GetAsync("drag-id", default).ConfigureAwait(false);
        fetched!.Metadata.Should().ContainKey("dashboard.layoutHints");
    }
}
