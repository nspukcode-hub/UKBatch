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
}
