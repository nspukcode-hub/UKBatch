using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Api.Approvals;
using UKBatch.Dashboard.Models.DagStatus;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure-C# unit tests for <see cref="ApprovalGateReconciler"/> — the approval-GATE node status derivation.
/// Gates have no JobExecution row; each gate carries its OWN recorded outcome, so the node colour is read
/// directly from the gate (pending → waiting, approved/auto-approved → completed, any other terminal
/// outcome → failed) rather than inferred from the batch's job-row aggregate.
/// </summary>
public sealed class ApprovalGateReconcilerTests
{
    private static ApprovalGateViewDto Pending(string stepId, string approvalId = "appr", string batchId = "br") => new()
    {
        ApprovalId = approvalId,
        BatchId = batchId,
        BatchStepId = stepId,
        Status = ApprovalRecordStatus.Pending,
        Outcome = null,
    };

    private static ApprovalGateViewDto Decided(string stepId, ApprovalRecordOutcome outcome,
        string approvalId = "appr", string batchId = "br") => new()
    {
        ApprovalId = approvalId,
        BatchId = batchId,
        BatchStepId = stepId,
        Status = ApprovalRecordStatus.Decided,
        Outcome = outcome,
    };

    [Fact]
    public void Pending_MarksAwaitingApproval()
    {
        var status = new Dictionary<string, JobStatus>(StringComparer.Ordinal);

        ApprovalGateReconciler.Apply([Pending("gate")], status);

        status["gate"].Should().Be(JobStatus.AwaitingApproval, "a pending gate is waiting");
    }

    [Fact]
    public void DecidedApproved_MarksCompleted()
    {
        var status = new Dictionary<string, JobStatus> { ["gate"] = JobStatus.AwaitingApproval };

        ApprovalGateReconciler.Apply([Decided("gate", ApprovalRecordOutcome.Approved)], status);

        status["gate"].Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void DecidedAutoApproved_MarksCompleted()
    {
        var status = new Dictionary<string, JobStatus>(StringComparer.Ordinal);

        ApprovalGateReconciler.Apply([Decided("gate", ApprovalRecordOutcome.AutoApproved)], status);

        status["gate"].Should().Be(JobStatus.Completed, "an auto-approved gate is a green completion");
    }

    [Theory]
    [InlineData(ApprovalRecordOutcome.Rejected)]
    [InlineData(ApprovalRecordOutcome.Dismissed)]
    [InlineData(ApprovalRecordOutcome.TimedOutFail)]
    [InlineData(ApprovalRecordOutcome.Cancelled)]
    [InlineData(ApprovalRecordOutcome.Interrupted)]
    public void DecidedNonApproval_MarksFailed(ApprovalRecordOutcome outcome)
    {
        var status = new Dictionary<string, JobStatus> { ["gate"] = JobStatus.AwaitingApproval };

        ApprovalGateReconciler.Apply([Decided("gate", outcome)], status);

        status["gate"].Should().Be(JobStatus.Failed, "any non-approval terminal outcome colours the gate Failed");
    }

    [Fact]
    public void EarlierApprovedGate_StaysGreen_WhenLaterGateFails()
    {
        // Over-reddening guard, now intrinsic: each gate carries its own outcome, so an earlier approved
        // gate is unaffected by a later gate's failure (no batch-level inference smears it red).
        var status = new Dictionary<string, JobStatus>(StringComparer.Ordinal);

        ApprovalGateReconciler.Apply(
            [Decided("g1", ApprovalRecordOutcome.Approved), Decided("g2", ApprovalRecordOutcome.Dismissed)],
            status);

        status["g1"].Should().Be(JobStatus.Completed, "an approved gate is not retro-failed by a later gate");
        status["g2"].Should().Be(JobStatus.Failed);
    }

    [Fact]
    public void MultipleGates_PendingAndDecided_EachColoursFromOwnOutcome()
    {
        var status = new Dictionary<string, JobStatus>
        {
            ["g1"] = JobStatus.AwaitingApproval,
            ["g2"] = JobStatus.AwaitingApproval,
        };

        // g1 decided (approved), g2 still pending.
        ApprovalGateReconciler.Apply([Decided("g1", ApprovalRecordOutcome.Approved), Pending("g2")], status);

        status["g1"].Should().Be(JobStatus.Completed);
        status["g2"].Should().Be(JobStatus.AwaitingApproval);
    }

    [Fact]
    public void ReApply_IsIdempotent()
    {
        // Decisions are immutable in the store, so re-reading the same feed must not change anything.
        var status = new Dictionary<string, JobStatus>(StringComparer.Ordinal);
        var gates = new[] { Decided("g1", ApprovalRecordOutcome.Approved), Decided("g2", ApprovalRecordOutcome.Rejected) };

        ApprovalGateReconciler.Apply(gates, status);
        var first = new Dictionary<string, JobStatus>(status, StringComparer.Ordinal);
        ApprovalGateReconciler.Apply(gates, status);

        status.Should().Equal(first, "re-applying the same immutable gate set is a no-op");
    }

    [Fact]
    public void NoGates_NoMutation()
    {
        var status = new Dictionary<string, JobStatus>(StringComparer.Ordinal);

        ApprovalGateReconciler.Apply([], status);

        status.Should().BeEmpty();
    }
}
