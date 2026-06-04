using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Runtime;   // Core's public batch exceptions
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// <see cref="EfBatchDefinitionStore"/> behavior on SQLite with semantic parity to
/// <c>InMemoryBatchDefinitionStore</c>: Create / Update (optimistic-concurrency conflict) / Delete
/// (idempotent) / Get / GetByName (source-scoped + whitespace asymmetry) / List (stable order, paging) /
/// Count. duplicate (Source,Name) → <see cref="BatchDefinitionDuplicateNameException"/>
/// vs duplicate-PK → generic <see cref="InvalidOperationException"/> (locks the SQLite
/// <c>DbExceptionClassifier</c> message-parse heuristic).
/// </summary>
public sealed class EfBatchDefinitionStoreTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfBatchDefinitionStore _store = default!;

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _store = new EfBatchDefinitionStore(_harness.Factory);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task CreateAsync_SetsVersionToOne_AndPersists()
    {
        var created = await _store.CreateAsync(TestData.BatchDef("def-1", "batch", version: 0), CancellationToken.None);
        created.Version.Should().Be(1, "CreateAsync sets Version=1 (mirrors InMemory)");

        var fetched = await _store.GetAsync("def-1", CancellationToken.None);
        fetched!.Name.Should().Be("batch");
        fetched.Version.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WhitespaceName_ThrowsArgument()
    {
        var act = async () => await _store.CreateAsync(TestData.BatchDef("def-1", "   "), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>("whitespace name is a programmer-time error (parity with InMemory)");
    }

    [Fact]
    public async Task CreateAsync_DuplicateSourceName_ThrowsBatchDefinitionDuplicateName()
    {
        // a (Source,Name) collision MUST map to the typed duplicate-NAME exception, not the
        // generic PK exception — this locks the SQLite DbExceptionClassifier message-parse branch.
        await _store.CreateAsync(TestData.BatchDef("id-1", "sameName", BatchSource.Dashboard), CancellationToken.None);

        var act = async () => await _store.CreateAsync(TestData.BatchDef("id-2", "sameName", BatchSource.Dashboard), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<BatchDefinitionDuplicateNameException>();
        ex.Which.Name.Should().Be("sameName");
        ex.Which.BatchSource.Should().Be(BatchSource.Dashboard);
        ex.Which.Should().BeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_DuplicatePrimaryKey_ThrowsGenericInvalidOperation_NotDuplicateName()
    {
        // a PK (id) collision is a generic programmer error, NOT the typed duplicate-NAME
        // exception. This is the dual-path lock — a future SQLite message reword breaks THIS test rather
        // than silently routing a name-collision to the PK branch (or vice-versa).
        await _store.CreateAsync(TestData.BatchDef("same-id", "name-A", BatchSource.Dashboard), CancellationToken.None);

        var act = async () => await _store.CreateAsync(TestData.BatchDef("same-id", "name-B", BatchSource.Dashboard), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Should().NotBeOfType<BatchDefinitionDuplicateNameException>(
            "a PK collision is a generic id-dup, distinct from a (Source,Name) collision");
        ex.Which.Message.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateAsync_SameNameDifferentSource_Allowed()
    {
        await _store.CreateAsync(TestData.BatchDef("id-1", "shared", BatchSource.Dashboard), CancellationToken.None);
        var act = async () => await _store.CreateAsync(TestData.BatchDef("id-2", "shared", BatchSource.Api), CancellationToken.None);
        await act.Should().NotThrowAsync("name uniqueness is per-source");
    }

    [Fact]
    public async Task UpdateAsync_BumpsVersion_AndPersistsEditableFields()
    {
        var created = await _store.CreateAsync(TestData.BatchDef("def-1", "original"), CancellationToken.None);
        var edited = created with { Name = "renamed", Schedule = "0 0 * * *" };

        var updated = await _store.UpdateAsync(edited, CancellationToken.None);
        updated.Version.Should().Be(2, "version bumps on update");

        var fetched = await _store.GetAsync("def-1", CancellationToken.None);
        fetched!.Name.Should().Be("renamed");
        fetched.Schedule.Should().Be("0 0 * * *");
        fetched.Version.Should().Be(2);
    }

    [Fact]
    public async Task UpdateAsync_StaleVersion_ThrowsConcurrencyConflict_WithStoreVersion()
    {
        var created = await _store.CreateAsync(TestData.BatchDef("def-cc", "batch"), CancellationToken.None);
        // Store is at version 1; submit a stale version 0.
        var stale = created with { Version = 0 };

        var act = async () => await _store.UpdateAsync(stale, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<BatchConcurrencyConflictException>();
        ex.Which.BatchDefinitionId.Should().Be("def-cc");
        ex.Which.StoreVersion.Should().Be(1, "the conflict carries the actual store version (fresh-context re-read)");
        ex.Which.CallerVersion.Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_MissingDefinition_ThrowsNotFound()
    {
        var act = async () => await _store.UpdateAsync(TestData.BatchDef("missing", "x", version: 1), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<BatchDefinitionNotFoundException>();
        ex.Which.BatchDefinitionId.Should().Be("missing");
    }

    [Fact]
    public async Task UpdateAsync_RenameToExistingName_ThrowsDuplicateName()
    {
        var def1 = await _store.CreateAsync(TestData.BatchDef("id-1", "name-A"), CancellationToken.None);
        await _store.CreateAsync(TestData.BatchDef("id-2", "name-B"), CancellationToken.None);

        var renamed = def1 with { Name = "name-B" };
        var act = async () => await _store.UpdateAsync(renamed, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<BatchDefinitionDuplicateNameException>();
        ex.Which.Name.Should().Be("name-B");
    }

    [Fact]
    public async Task UpdateAsync_DoesNotChangeCreatedMetadata()
    {
        var created = await _store.CreateAsync(TestData.BatchDef("def-1", "batch", createdBy: "creator"), CancellationToken.None);
        var edited = created with { Name = "renamed" };
        await _store.UpdateAsync(edited, CancellationToken.None);

        var fetched = await _store.GetAsync("def-1", CancellationToken.None);
        fetched!.CreatedBy.Should().Be("creator", "CopyEditableFields excludes CreatedBy/CreatedAtUtc");
        fetched.CreatedAtUtc.Should().Be(created.CreatedAtUtc);
    }

    [Fact]
    public async Task DeleteAsync_ExistingDefinition_Removes()
    {
        await _store.CreateAsync(TestData.BatchDef("def-1", "batch"), CancellationToken.None);
        await _store.DeleteAsync("def-1", CancellationToken.None);

        (await _store.GetAsync("def-1", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_MissingDefinition_Idempotent_NoThrow()
    {
        var act = async () => await _store.DeleteAsync("never-existed", CancellationToken.None);
        await act.Should().NotThrowAsync("delete is idempotent (silent if absent)");
    }

    [Fact]
    public async Task DeleteAsync_ThenRecreateSameName_Allowed()
    {
        await _store.CreateAsync(TestData.BatchDef("id-1", "batch", BatchSource.Dashboard), CancellationToken.None);
        await _store.DeleteAsync("id-1", CancellationToken.None);
        // The (Source,Name) slot must be freed so a recreate succeeds.
        var act = async () => await _store.CreateAsync(TestData.BatchDef("id-2", "batch", BatchSource.Dashboard), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetByNameAsync_SourceScoped()
    {
        await _store.CreateAsync(TestData.BatchDef("id-d", "shared", BatchSource.Dashboard), CancellationToken.None);
        await _store.CreateAsync(TestData.BatchDef("id-a", "shared", BatchSource.Api), CancellationToken.None);

        var dashboard = await _store.GetByNameAsync("shared", BatchSource.Dashboard, CancellationToken.None);
        dashboard!.Id.Should().Be("id-d");

        var api = await _store.GetByNameAsync("shared", BatchSource.Api, CancellationToken.None);
        api!.Id.Should().Be("id-a");
    }

    [Fact]
    public async Task GetByNameAsync_WhitespaceName_ReturnsNull_WhitespaceAsymmetry()
    {
        // Whitespace asymmetry: CreateAsync rejects whitespace (programmer error), but the lookup
        // boundary returns null (runtime input may legitimately be invalid) — mirrors InMemory.
        await _store.CreateAsync(TestData.BatchDef("id-1", "batch", BatchSource.Dashboard), CancellationToken.None);
        var result = await _store.GetByNameAsync("   ", BatchSource.Dashboard, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_StableOrder_AndPaging_BySource()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.CreateAsync(TestData.BatchDef($"id-{i:D2}", $"batch-{i}", BatchSource.Dashboard), CancellationToken.None);
        }
        await _store.CreateAsync(TestData.BatchDef("other-source", "x", BatchSource.Api), CancellationToken.None);

        var page = await _store.ListAsync(BatchSource.Dashboard, offset: 1, limit: 2, CancellationToken.None);
        page.Select(d => d.Id).Should().Equal(new[] { "id-01", "id-02" }, "stable Id-ascending order, source-scoped paging");
    }

    [Fact]
    public async Task CountAsync_BySource()
    {
        await _store.CreateAsync(TestData.BatchDef("id-1", "a", BatchSource.Dashboard), CancellationToken.None);
        await _store.CreateAsync(TestData.BatchDef("id-2", "b", BatchSource.Dashboard), CancellationToken.None);
        await _store.CreateAsync(TestData.BatchDef("id-3", "c", BatchSource.Api), CancellationToken.None);

        (await _store.CountAsync(BatchSource.Dashboard, CancellationToken.None)).Should().Be(2);
        (await _store.CountAsync(BatchSource.Api, CancellationToken.None)).Should().Be(1);
        (await _store.CountAsync(BatchSource.Code, CancellationToken.None)).Should().Be(0);
    }
}
