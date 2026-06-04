using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Storage.EntityFrameworkCore.Entities;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// Round-trip the new <see cref="BatchDefinition.Metadata"/> column through
/// the EF Core SQLite adapter. <para><b>regression lock:</b>
/// <c>CopyEditableFields_PreservesMetadata_DragPersistRoundTrip</c> — if the engineer ever forgets
/// to add <c>Metadata</c> to <see cref="UKBatch.Storage.EntityFrameworkCore.Mapping.BatchDefinitionMapper.CopyEditableFields"/>,
/// drag-persist silently no-ops + this test fails.</para>
/// <para><b>Opsiyon B parity:</b> null Metadata round-trips through entity (empty dict)
/// back to null on read — operator-invisible asymmetry verified.</para>
/// </summary>
public sealed class BatchDefinitionMetadataRoundTripTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfBatchDefinitionStore _store = default!;

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _store = new EfBatchDefinitionStore(_harness.Factory);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    // ── lock — drag-persist round-trip ──────

    [Fact]
    public async Task CopyEditableFields_PreservesMetadata_DragPersistRoundTrip()
    {
        // The silent data-loss scenario: create a batch, simulate operator drag-persist by
        // calling UpdateAsync with NEW Metadata, then re-read. If Metadata is NOT copied in
        // CopyEditableFields, the entity flushes unchanged → re-read shows pre-drag hints.
        var def = TestData.BatchDef("drag-id", "drag-batch");
        var created = await _store.CreateAsync(def, CancellationToken.None);

        var newHints = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dashboard.layoutHints"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["s1"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 120.0, ["y"] = 80.0 },
                ["s2"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 320.0, ["y"] = 80.0 },
            },
        };
        var withHints = created with { Metadata = newHints };
        await _store.UpdateAsync(withHints, CancellationToken.None);

        var fetched = await _store.GetAsync("drag-id", CancellationToken.None);
        fetched!.Metadata.Should().NotBeNull(
 " lock: drag-persist MUST round-trip Metadata via CopyEditableFields");
        fetched.Metadata.Should().ContainKey("dashboard.layoutHints");
    }

    // ── null→empty→null round-trip parity (Opsiyon B) ──────────

    [Fact]
    public async Task BatchDefinitionMapper_NullMetadata_RoundTripsAsNull_NotEmptyDict()
    {
        // Opsiyon B contract: ToEntity normalizes null → empty dict (JsonColumn factory requires
        // non-null); ToModel reverses (empty → null) for parity with InMemory's nullable shape.
        // Operator-invisible asymmetry.
        var def = TestData.BatchDef("null-meta", "no-hints");
        def.Metadata.Should().BeNull("test setup: starts with null Metadata");
        await _store.CreateAsync(def, CancellationToken.None);

        var fetched = await _store.GetAsync("null-meta", CancellationToken.None);
        fetched!.Metadata.Should().BeNull(
 "empty dict on entity reverses to null on read — operator-invisible asymmetry");
    }

    // ── Metadata persists across Create + Read (basic CRUD) ──────────

    [Fact]
    public async Task EfStore_BatchDefinition_CreateWithMetadata_PersistsAcrossRead()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dashboard.layoutHints"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["s1"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 100.0, ["y"] = 200.0 },
            },
        };
        var def = TestData.BatchDef("create-meta", "with-hints") with { Metadata = metadata };
        await _store.CreateAsync(def, CancellationToken.None);

        var fetched = await _store.GetAsync("create-meta", CancellationToken.None);
        fetched!.Metadata.Should().NotBeNull();
        fetched.Metadata.Should().ContainKey("dashboard.layoutHints");
    }

    // ── Update REMOVES Metadata (set to null) — reset path ───────

    [Fact]
    public async Task EfStore_BatchDefinition_UpdateClearMetadata_PersistsAsNull()
    {
        // reset = key removal. When Detail.razor ResetLayoutHintsAsync calls UpdateBatchAsync
        // with Metadata=null (no foreign keys remain), the store MUST persist the absence — i.e.
        // re-read shows Metadata=null.
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dashboard.layoutHints"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["s1"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 100.0, ["y"] = 200.0 },
            },
        };
        var def = TestData.BatchDef("reset-meta", "to-reset") with { Metadata = metadata };
        var created = await _store.CreateAsync(def, CancellationToken.None);

        // Operator clicks Reset → ResetLayoutHintsAsync builds UpdateBatchRequest with Metadata=null.
        var cleared = created with { Metadata = null };
        await _store.UpdateAsync(cleared, CancellationToken.None);

        var fetched = await _store.GetAsync("reset-meta", CancellationToken.None);
        fetched!.Metadata.Should().BeNull(
 "reset clears Metadata — null on the wire becomes null on re-read");
    }

    // ── Empty Metadata dict round-trips as null (Opsiyon B parity) ───

    [Fact]
    public async Task EfStore_BatchDefinition_EmptyMetadataDict_RoundTripsAsNull()
    {
        // an empty (but non-null) dict on input round-trips to null on read — the
        // operator-invisible asymmetry. reset path can send Metadata={} as a safety variant.
        var def = TestData.BatchDef("empty-meta", "empty") with
        {
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal), // empty, non-null
        };
        await _store.CreateAsync(def, CancellationToken.None);

        var fetched = await _store.GetAsync("empty-meta", CancellationToken.None);
        fetched!.Metadata.Should().BeNull(
 " Opsiyon B: empty dict normalizes to null on read");
    }

    // ── Pre-migration NULL Metadata column reads back as null ──

    [Fact]
    public async Task BatchDefinition_PreMigrationNullMetadata_ReadsAsNull()
    {
        // Backward-compat: a row written before the AddBatchDefinitionMetadata migration carries a
        // DB-NULL Metadata column. We simulate it by inserting a raw entity with Metadata=null
        // directly (bypassing the mapper's null→empty normalize), then read through the store and
        // assert ToModel surfaces it as null (the nullable column + ToModel empty/null guard cover
        // both the DB-NULL and empty-dict shapes).
        await using (var db = await _harness.NewContextAsync())
        {
            db.BatchDefinitions.Add(new BatchDefinitionEntity
            {
                Id = "pre-migration",
                Name = "legacy-row",
                Source = BatchSource.Dashboard,
                Steps = Array.Empty<BatchStep>(),
                FailurePolicy = BatchFailurePolicy.StopOnFailure,
                OnFailureSteps = Array.Empty<BatchStep>(),
                CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Version = 1,
                Metadata = null, // DB-NULL — the pre-migration shape.
            });
            await db.SaveChangesAsync();
        }

        var fetched = await _store.GetAsync("pre-migration", CancellationToken.None);
        fetched!.Metadata.Should().BeNull(
            "a pre-migration DB-NULL Metadata column reads back as null (backward-compat)");
    }
}
