using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// <c>ApprovalGateService.ListForBatchAsync</c> maps each <see cref="PersistedApprovalGate"/> in a run to
/// a focused <see cref="ApprovalGateView"/> carrying the gate's OWN status + decided outcome — the read a
/// status renderer needs to colour a gate node (the gate has no <see cref="JobExecution"/> row, so the
/// row aggregate cannot see its outcome). Verifies a mix: a reserved/legacy Dismissed, a Rejected, an
/// Approved, a Pending.
/// </summary>
public class ApprovalGateServiceListForBatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ApprovalGateService NewService(IApprovalGateStore store) =>
        // ListForBatchAsync reads only the store; the batch lookup is never consulted (stub it).
        new(new FakeTimeProvider(T0), Substitute.For<IBatchDefinitionLookup>(), store, NullLogger<ApprovalGateService>.Instance);

    private static PersistedApprovalGate Gate(string approvalId, string stepId, DateTimeOffset pendingSince, ApprovalRecordStatus status, ApprovalRecordOutcome? outcome) => new()
    {
        ApprovalId = approvalId,
        BatchId = "run-1",
        BatchStepId = stepId,
        Config = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "admin" }, OnTimeout = ApprovalTimeoutAction.Hold },
        Status = status,
        PendingSinceUtc = pendingSince,
        Outcome = outcome,
    };

    [Fact]
    public async Task ListForBatchAsync_MapsStatusAndOutcome_ForAMix()
    {
        var store = new InMemoryApprovalGateStore();
        await store.SaveAsync(Gate("g-approved", "step-a", T0.AddMinutes(1), ApprovalRecordStatus.Decided, ApprovalRecordOutcome.Approved), CancellationToken.None);
        await store.SaveAsync(Gate("g-rejected", "step-b", T0.AddMinutes(2), ApprovalRecordStatus.Decided, ApprovalRecordOutcome.Rejected), CancellationToken.None);
        await store.SaveAsync(Gate("g-dismissed", "step-c", T0.AddMinutes(3), ApprovalRecordStatus.Decided, ApprovalRecordOutcome.Dismissed), CancellationToken.None);
        await store.SaveAsync(Gate("g-pending", "step-d", T0.AddMinutes(4), ApprovalRecordStatus.Pending, null), CancellationToken.None);

        var views = await NewService(store).ListForBatchAsync("run-1", CancellationToken.None);

        views.Should().HaveCount(4);

        var approved = views.Single(v => v.ApprovalId == "g-approved");
        approved.BatchStepId.Should().Be("step-a");
        approved.Status.Should().Be(ApprovalRecordStatus.Decided);
        approved.Outcome.Should().Be(ApprovalRecordOutcome.Approved);

        var rejected = views.Single(v => v.ApprovalId == "g-rejected");
        rejected.Status.Should().Be(ApprovalRecordStatus.Decided);
        rejected.Outcome.Should().Be(ApprovalRecordOutcome.Rejected);

        var dismissed = views.Single(v => v.ApprovalId == "g-dismissed");
        dismissed.Status.Should().Be(ApprovalRecordStatus.Decided);
        dismissed.Outcome.Should().Be(ApprovalRecordOutcome.Dismissed);

        var pending = views.Single(v => v.ApprovalId == "g-pending");
        pending.Status.Should().Be(ApprovalRecordStatus.Pending);
        pending.Outcome.Should().BeNull("a pending gate has no decided outcome yet");

        views.Should().AllSatisfy(v => v.BatchId.Should().Be("run-1"), "every view is scoped to the queried run");
    }

    [Fact]
    public async Task ListForBatchAsync_UnknownRun_ReturnsEmpty()
    {
        var store = new InMemoryApprovalGateStore();
        await store.SaveAsync(Gate("g1", "step-a", T0, ApprovalRecordStatus.Pending, null), CancellationToken.None);

        (await NewService(store).ListForBatchAsync("run-does-not-exist", CancellationToken.None))
            .Should().BeEmpty();
    }
}
