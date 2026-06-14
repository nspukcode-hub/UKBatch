using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// <see cref="EfApprovalGateStore"/> behavior on SQLite: Save (idempotent upsert) / Get / ListPending /
/// RecordOutcome: RecordOutcome on an ABSENT gate THROWS (the direct-caller 404 path); a
/// Cancelled outcome persists and is excluded from ListPending; Interrupted (reaper) outcome persists.
///
/// </summary>
public sealed class EfApprovalGateStoreTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfApprovalGateStore _store = default!;

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _store = new EfApprovalGateStore(_harness.Factory);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task SaveAsync_NewGate_Inserts()
    {
        await _store.SaveAsync(TestData.Gate("g1", batchDefinitionId: "def-A"), CancellationToken.None);

        var fetched = await _store.GetAsync("g1", CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.BatchDefinitionId.Should().Be("def-A");
        fetched.Status.Should().Be(ApprovalRecordStatus.Pending);
    }

    [Fact]
    public async Task SaveAsync_ExistingGate_OverwritesIdempotently()
    {
        await _store.SaveAsync(TestData.Gate("g1", batchId: "run-1"), CancellationToken.None);
        // Re-save the same id with a different batch (upsert-overwrite).
        await _store.SaveAsync(TestData.Gate("g1", batchId: "run-2"), CancellationToken.None);

        var fetched = await _store.GetAsync("g1", CancellationToken.None);
        fetched!.BatchId.Should().Be("run-2");

        // Still exactly one row.
        var pending = await _store.ListPendingAsync(CancellationToken.None);
        pending.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAsync_MissingGate_ReturnsNull()
    {
        (await _store.GetAsync("nope", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ListPendingAsync_ReturnsOnlyPending_StableOrder()
    {
        await _store.SaveAsync(TestData.Gate("g3", pendingSinceUtc: T0.AddMinutes(3)), CancellationToken.None);
        await _store.SaveAsync(TestData.Gate("g1", pendingSinceUtc: T0.AddMinutes(1)), CancellationToken.None);
        await _store.SaveAsync(TestData.Gate("g2", pendingSinceUtc: T0.AddMinutes(2)), CancellationToken.None);
        // A decided gate must NOT appear.
        await _store.SaveAsync(TestData.Gate("decided", status: ApprovalRecordStatus.Decided, outcome: ApprovalRecordOutcome.Approved), CancellationToken.None);

        var pending = await _store.ListPendingAsync(CancellationToken.None);
        pending.Select(g => g.ApprovalId).Should().Equal(new[] { "g1", "g2", "g3" }, "ordered by PendingSinceUtc then ApprovalId");
    }

    [Fact]
    public async Task RecordOutcomeAsync_OnPendingGate_TransitionsToDecided()
    {
        await _store.SaveAsync(TestData.Gate("g1"), CancellationToken.None);
        await _store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Approved, "admin@x", T0.AddMinutes(5), "looks good", CancellationToken.None);

        var fetched = await _store.GetAsync("g1", CancellationToken.None);
        fetched!.Status.Should().Be(ApprovalRecordStatus.Decided);
        fetched.Outcome.Should().Be(ApprovalRecordOutcome.Approved);
        fetched.DecidedBy.Should().Be("admin@x");
        fetched.DecidedAtUtc.Should().Be(T0.AddMinutes(5));
        fetched.Note.Should().Be("looks good");
    }

    [Fact]
    public async Task RecordOutcomeAsync_OnAlreadyDecidedGate_Throws_AndDoesNotOverwrite()
    {
        // Terminal outcomes are immutable: once a gate is Decided, a second decision must throw
        // ApprovalAlreadyDecidedException and leave the original audit record (outcome + decider) intact.
        await _store.SaveAsync(TestData.Gate("g1"), CancellationToken.None);
        await _store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Approved, "admin", T0.AddMinutes(1), "ok", CancellationToken.None);

        var act = async () => await _store.RecordOutcomeAsync(
            "g1", ApprovalRecordOutcome.Rejected, "attacker", T0.AddMinutes(2), "tamper", CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<ApprovalAlreadyDecidedException>()).Which;
        ex.ApprovalId.Should().Be("g1");
        ex.ExistingOutcome.Should().Be(ApprovalRecordOutcome.Approved);

        var fetched = await _store.GetAsync("g1", CancellationToken.None);
        fetched!.Outcome.Should().Be(ApprovalRecordOutcome.Approved, "the first decision wins — no overwrite");
        fetched.DecidedBy.Should().Be("admin");
        fetched.Note.Should().Be("ok");
    }

    [Fact]
    public async Task RecordOutcomeAsync_OnAbsentGate_Throws()
    {
        // direct-caller contract: the dashboard approving a truly-missing id maps to 404 via
        // the typed throw. (ApprovalGateService downgrades this to a warn-log for its never-persisted
        // crash-orphan path — but the STORE itself keeps the throw.)
        var act = async () => await _store.RecordOutcomeAsync("ghost", ApprovalRecordOutcome.Approved, "x", T0, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task RecordOutcomeAsync_Cancelled_PersistsAndExcludedFromListPending()
    {
        // a Cancelled gate must leave Pending so the merge never resurrects it.
        await _store.SaveAsync(TestData.Gate("g1"), CancellationToken.None);
        await _store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Cancelled, "<cancelled>", T0.AddMinutes(1), null, CancellationToken.None);

        var fetched = await _store.GetAsync("g1", CancellationToken.None);
        fetched!.Status.Should().Be(ApprovalRecordStatus.Decided);
        fetched.Outcome.Should().Be(ApprovalRecordOutcome.Cancelled);

        var pending = await _store.ListPendingAsync(CancellationToken.None);
        pending.Should().BeEmpty("a Cancelled gate is terminal — no ghost in the pending feed");
    }

    [Fact]
    public async Task RecordOutcomeAsync_Interrupted_PersistsAsTerminal()
    {
        // The reaper writes Interrupted (a value no live decision path produces).
        await _store.SaveAsync(TestData.Gate("g1"), CancellationToken.None);
        await _store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Interrupted, "<reaper>", T0.AddHours(1), "reaped", CancellationToken.None);

        var fetched = await _store.GetAsync("g1", CancellationToken.None);
        fetched!.Outcome.Should().Be(ApprovalRecordOutcome.Interrupted);
        (await _store.ListPendingAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task RecordOutcomeAsync_NullDecidedBy_Throws()
    {
        await _store.SaveAsync(TestData.Gate("g1"), CancellationToken.None);
        var act = async () => await _store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Approved, null!, T0, null, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RecordOutcomeAsync_Dismissed_PersistsAsTerminal_ReadsBackNoMigration()
    {
        // A record carrying the reserved/legacy Dismissed outcome persists as a terminal decision
        // (enum-as-string, so the value round-trips with no schema change) and never appears in the
        // pending feed.
        await _store.SaveAsync(TestData.Gate("g1"), CancellationToken.None);
        await _store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Dismissed, "ops@x", T0.AddMinutes(2), "undecidable", CancellationToken.None);

        var fetched = await _store.GetAsync("g1", CancellationToken.None);
        fetched!.Status.Should().Be(ApprovalRecordStatus.Decided);
        fetched.Outcome.Should().Be(ApprovalRecordOutcome.Dismissed);
        fetched.DecidedBy.Should().Be("ops@x");
        fetched.Note.Should().Be("undecidable");
        (await _store.ListPendingAsync(CancellationToken.None)).Should().BeEmpty();

        await using var db = await _harness.NewContextAsync();
        var rawOutcome = await db.Database
            .SqlQueryRaw<string>("SELECT Outcome AS Value FROM ApprovalGates WHERE ApprovalId = 'g1'")
            .SingleAsync();
        rawOutcome.Should().Be("Dismissed", "the new outcome persists as its NAME — no migration needed");
    }

    [Fact]
    public async Task OutcomeEnum_RoundTripsAsName_NotInteger()
    {
        // parity: nullable Outcome enum serializes as a NAME (the new Cancelled/Interrupted values
        // round-trip non-breakingly because the column is enum-as-string).
        await _store.SaveAsync(TestData.Gate("g1"), CancellationToken.None);
        await _store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.AutoApproved, "<system>", T0, null, CancellationToken.None);

        await using var db = await _harness.NewContextAsync();
        var rawOutcome = await db.Database
            .SqlQueryRaw<string>("SELECT Outcome AS Value FROM ApprovalGates WHERE ApprovalId = 'g1'")
            .SingleAsync();
        rawOutcome.Should().Be("AutoApproved", "the Outcome enum persists as its NAME, not an integer");
    }

    // ListByBatchAsync — the by-run query a status renderer uses to colour every gate node from its OWN
    // decided outcome. Assertions are mirrored textually in InMemoryApprovalGateStoreTests so both
    // adapters enforce the same contract (the drop-in proof) — no migration is needed (BatchId is mapped).

    [Fact]
    public async Task ListByBatchAsync_ReturnsPendingAndDecided_ForTheRun_InStableOrder()
    {
        await _store.SaveAsync(TestData.Gate("g-pending", batchId: "run-1", pendingSinceUtc: T0.AddMinutes(2)), CancellationToken.None);
        await _store.SaveAsync(
            TestData.Gate("g-decided", batchId: "run-1", pendingSinceUtc: T0.AddMinutes(1), status: ApprovalRecordStatus.Decided, outcome: ApprovalRecordOutcome.Dismissed),
            CancellationToken.None);

        var gates = await _store.ListByBatchAsync("run-1", CancellationToken.None);

        gates.Select(g => g.ApprovalId).Should().Equal(new[] { "g-decided", "g-pending" },
            "pending AND decided are returned, ordered by PendingSinceUtc then ApprovalId");
        gates.Single(g => g.ApprovalId == "g-decided").Outcome.Should().Be(ApprovalRecordOutcome.Dismissed);
        gates.Single(g => g.ApprovalId == "g-pending").Status.Should().Be(ApprovalRecordStatus.Pending);
    }

    [Fact]
    public async Task ListByBatchAsync_UnknownBatch_ReturnsEmpty()
    {
        await _store.SaveAsync(TestData.Gate("g1", batchId: "run-1"), CancellationToken.None);
        (await _store.ListByBatchAsync("run-does-not-exist", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListByBatchAsync_IsRunScoped_DoesNotReturnAnotherRunsGates()
    {
        await _store.SaveAsync(TestData.Gate("g-a", batchId: "run-A"), CancellationToken.None);
        await _store.SaveAsync(TestData.Gate("g-b", batchId: "run-B"), CancellationToken.None);

        var gates = await _store.ListByBatchAsync("run-A", CancellationToken.None);
        gates.Select(g => g.ApprovalId).Should().Equal(new[] { "g-a" }, "the query is scoped to one run");
    }
}
