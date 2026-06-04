using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// Name-index tests for <see cref="InMemoryBatchDefinitionStore.GetByNameAsync"/> +
/// name-uniqueness enforcement on Create/Update + rename atomicity.
/// </summary>
public class InMemoryBatchDefinitionStoreNameTests
{
    private static BatchDefinition NewDef(string id, string name, BatchSource src = BatchSource.Dashboard) => new()
    {
        Id = id,
        Name = name,
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
    public async Task GetByNameAsync_ReturnsDefinition_WhenExists()
    {
        var store = new InMemoryBatchDefinitionStore();
        await store.CreateAsync(NewDef("b1", "alpha"), default);
        var fetched = await store.GetByNameAsync("alpha", BatchSource.Dashboard, default);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be("b1");
    }

    [Fact]
    public async Task GetByNameAsync_ReturnsNull_WhenAbsent()
    {
        var store = new InMemoryBatchDefinitionStore();
        var fetched = await store.GetByNameAsync("missing", BatchSource.Dashboard, default);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task GetByNameAsync_ReturnsNull_WhenWhitespaceName()
    {
        var store = new InMemoryBatchDefinitionStore();
        // Whitespace asymmetry preserved — lookup boundary tolerates whitespace, returns null.
        var fetched = await store.GetByNameAsync("   ", BatchSource.Dashboard, default);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task GetByNameAsync_Throws_WhenNullOrEmptyName()
    {
        var store = new InMemoryBatchDefinitionStore();
        Func<Task> emptyCall = () => store.GetByNameAsync("", BatchSource.Dashboard, default);
        await emptyCall.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetByNameAsync_RespectsSourceScope()
    {
        var store = new InMemoryBatchDefinitionStore();
        // Same NAME across Dashboard + Api — both retrievable.
        await store.CreateAsync(NewDef("b1", "shared", BatchSource.Dashboard), default);
        await store.CreateAsync(NewDef("b2", "shared", BatchSource.Api), default);
        var dash = await store.GetByNameAsync("shared", BatchSource.Dashboard, default);
        var api = await store.GetByNameAsync("shared", BatchSource.Api, default);
        dash!.Id.Should().Be("b1");
        api!.Id.Should().Be("b2");
    }

    [Fact]
    public async Task CreateAsync_Throws_OnDuplicateNameWithinSource()
    {
        var store = new InMemoryBatchDefinitionStore();
        await store.CreateAsync(NewDef("b1", "shared", BatchSource.Dashboard), default);
        Func<Task> act = () => store.CreateAsync(NewDef("b2", "shared", BatchSource.Dashboard), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists in source Dashboard*");
    }

    [Fact]
    public async Task CreateAsync_AllowsDuplicateName_AcrossDifferentSources()
    {
        var store = new InMemoryBatchDefinitionStore();
        Func<Task> act = async () =>
        {
            await store.CreateAsync(NewDef("b1", "shared", BatchSource.Dashboard), default);
            await store.CreateAsync(NewDef("b2", "shared", BatchSource.Api), default);
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateAsync_RollsBackId_OnNameCollision()
    {
        var store = new InMemoryBatchDefinitionStore();
        await store.CreateAsync(NewDef("b1", "shared", BatchSource.Dashboard), default);
        // Try to add a different id with the same name — should throw AND not leave the new id present.
        try
        {
            await store.CreateAsync(NewDef("b2", "shared", BatchSource.Dashboard), default);
        }
        catch (InvalidOperationException) { /* expected */ }
        // b2's id should NOT be retrievable after the rollback.
        (await store.GetAsync("b2", default)).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_AllowsRename_WhenNewNameFree()
    {
        var store = new InMemoryBatchDefinitionStore();
        var created = await store.CreateAsync(NewDef("b1", "old"), default);
        var renamed = await store.UpdateAsync(created with { Name = "new" }, default);
        renamed.Name.Should().Be("new");
        (await store.GetByNameAsync("old", BatchSource.Dashboard, default)).Should().BeNull();
        (await store.GetByNameAsync("new", BatchSource.Dashboard, default))!.Id.Should().Be("b1");
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenRenameCollidesWithinSource()
    {
        var store = new InMemoryBatchDefinitionStore();
        var a = await store.CreateAsync(NewDef("ba", "alpha"), default);
        await store.CreateAsync(NewDef("bb", "beta"), default);
        Func<Task> act = () => store.UpdateAsync(a with { Name = "beta" }, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*existing name*");
    }

    // <summary>: 16 parallel rename calls — exactly one wins, name index ends in a consistent state.</summary>
    [Fact]
    public async Task UpdateAsync_ConcurrentRename_NeverLeavesPartialState()
    {
        var store = new InMemoryBatchDefinitionStore();
        var created = await store.CreateAsync(NewDef("bX", "src"), default);

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.UpdateAsync(created with { Name = $"target-{i}" }, default);
            }
            catch (InvalidOperationException)
            {
                // expected — only one rename succeeds due to optimistic concurrency on Version.
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        // Exactly one rename win: the entry should be retrievable under exactly one target-* name.
        var final = await store.GetAsync("bX", default);
        final.Should().NotBeNull();
        final!.Name.Should().StartWith("target-");
        // The old "src" name MUST be free.
        (await store.GetByNameAsync("src", BatchSource.Dashboard, default)).Should().BeNull();
        // The winning name MUST resolve back to bX.
        (await store.GetByNameAsync(final.Name, BatchSource.Dashboard, default))!.Id.Should().Be("bX");
    }
}
