using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// <see cref="InMemoryApprovalGateStore"/> decision-record behavior. The decided-guard scenario here is
/// textually identical to the EF store's parity test so both adapters enforce the same contract: a gate's
/// terminal outcome is an immutable audit fact.
/// </summary>
public sealed class InMemoryApprovalGateStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PersistedApprovalGate Gate(string approvalId) => new()
    {
        ApprovalId = approvalId,
        BatchId = "batch-1",
        BatchStepId = "step-1",
        Config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { "admin" },
            OnTimeout = ApprovalTimeoutAction.Hold,
        },
        Status = ApprovalRecordStatus.Pending,
        PendingSinceUtc = T0,
    };

    [Fact]
    public async Task RecordOutcomeAsync_OnAlreadyDecidedGate_Throws_AndDoesNotOverwrite()
    {
        // Terminal outcomes are immutable: once a gate is Decided, a second decision must throw
        // ApprovalAlreadyDecidedException and leave the original audit record (outcome + decider) intact.
        var store = new InMemoryApprovalGateStore();
        await store.SaveAsync(Gate("g1"), CancellationToken.None);
        await store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Approved, "admin", T0.AddMinutes(1), "ok", CancellationToken.None);

        var act = async () => await store.RecordOutcomeAsync(
            "g1", ApprovalRecordOutcome.Rejected, "attacker", T0.AddMinutes(2), "tamper", CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<ApprovalAlreadyDecidedException>()).Which;
        ex.ApprovalId.Should().Be("g1");
        ex.ExistingOutcome.Should().Be(ApprovalRecordOutcome.Approved);

        var fetched = await store.GetAsync("g1", CancellationToken.None);
        fetched!.Outcome.Should().Be(ApprovalRecordOutcome.Approved, "the first decision wins — no overwrite");
        fetched.DecidedBy.Should().Be("admin");
        fetched.Note.Should().Be("ok");
    }

    [Fact]
    public async Task RecordOutcomeAsync_OnAbsentGate_Throws()
    {
        // Parity with the EF store's direct-caller 404 contract — absent fires before the decided-check.
        var store = new InMemoryApprovalGateStore();
        var act = async () => await store.RecordOutcomeAsync(
            "ghost", ApprovalRecordOutcome.Approved, "x", T0, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task RecordOutcomeAsync_Dismissed_PersistsAsTerminal_ExcludedFromPending()
    {
        // Parity with the EF store: a record carrying the reserved/legacy Dismissed outcome persists as a
        // terminal decision and leaves the pending feed.
        var store = new InMemoryApprovalGateStore();
        await store.SaveAsync(Gate("g1"), CancellationToken.None);
        await store.RecordOutcomeAsync("g1", ApprovalRecordOutcome.Dismissed, "ops@x", T0.AddMinutes(2), "undecidable", CancellationToken.None);

        var fetched = await store.GetAsync("g1", CancellationToken.None);
        fetched!.Status.Should().Be(ApprovalRecordStatus.Decided);
        fetched.Outcome.Should().Be(ApprovalRecordOutcome.Dismissed);
        fetched.DecidedBy.Should().Be("ops@x");
        fetched.Note.Should().Be("undecidable");
        (await store.ListPendingAsync(CancellationToken.None)).Should().BeEmpty();
    }

    // ListByBatchAsync — the by-run query that lets a status renderer colour every gate node from its
    // OWN decided outcome (a gate has no JobExecution row). The assertions here are mirrored textually in
    // the EF store's parity test so both adapters enforce the same contract.

    private static PersistedApprovalGate GateFor(string approvalId, string batchId, DateTimeOffset pendingSince, ApprovalRecordStatus status = ApprovalRecordStatus.Pending, ApprovalRecordOutcome? outcome = null) => new()
    {
        ApprovalId = approvalId,
        BatchId = batchId,
        BatchStepId = "step-1",
        Config = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "admin" }, OnTimeout = ApprovalTimeoutAction.Hold },
        Status = status,
        PendingSinceUtc = pendingSince,
        Outcome = outcome,
    };

    [Fact]
    public async Task ListByBatchAsync_ReturnsPendingAndDecided_ForTheRun_InStableOrder()
    {
        // A renderer needs EVERY gate of the run regardless of decision state: a pending gate AND a
        // decided one (so the decided node can go red/green). Order is PendingSinceUtc then ApprovalId.
        var store = new InMemoryApprovalGateStore();
        await store.SaveAsync(GateFor("g-pending", "run-1", T0.AddMinutes(2)), CancellationToken.None);
        await store.SaveAsync(GateFor("g-decided", "run-1", T0.AddMinutes(1), ApprovalRecordStatus.Decided, ApprovalRecordOutcome.Dismissed), CancellationToken.None);

        var gates = await store.ListByBatchAsync("run-1", CancellationToken.None);

        gates.Select(g => g.ApprovalId).Should().Equal(new[] { "g-decided", "g-pending" },
            "pending AND decided are returned, ordered by PendingSinceUtc then ApprovalId");
        gates.Single(g => g.ApprovalId == "g-decided").Outcome.Should().Be(ApprovalRecordOutcome.Dismissed);
        gates.Single(g => g.ApprovalId == "g-pending").Status.Should().Be(ApprovalRecordStatus.Pending);
    }

    [Fact]
    public async Task ListByBatchAsync_UnknownBatch_ReturnsEmpty()
    {
        var store = new InMemoryApprovalGateStore();
        await store.SaveAsync(GateFor("g1", "run-1", T0), CancellationToken.None);

        (await store.ListByBatchAsync("run-does-not-exist", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListByBatchAsync_IsRunScoped_DoesNotReturnAnotherRunsGates()
    {
        var store = new InMemoryApprovalGateStore();
        await store.SaveAsync(GateFor("g-a", "run-A", T0), CancellationToken.None);
        await store.SaveAsync(GateFor("g-b", "run-B", T0), CancellationToken.None);

        var gates = await store.ListByBatchAsync("run-A", CancellationToken.None);
        gates.Select(g => g.ApprovalId).Should().Equal(new[] { "g-a" }, "the query is scoped to one run");
    }
}
