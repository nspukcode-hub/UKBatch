using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Models.DagStatus;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure-C# unit tests for <see cref="ApprovalGateReconciler"/> — the approval-GATE node status derivation
/// Gates have no JobExecution row; status comes from the PendingApproval set.
/// </summary>
public sealed class ApprovalGateReconcilerTests
{
    private static HashSet<string> Set(params string[] ids) => new(ids, StringComparer.Ordinal);

    [Fact]
    public void NewlyPending_MarksAwaitingApproval()
    {
        var resolved = Set();
        var status = new Dictionary<string, JobStatus>(StringComparer.Ordinal);

        ApprovalGateReconciler.Apply(Set("gate"), previousAwaiting: Set(), batchFailed: false, resolved, status);

        status["gate"].Should().Be(JobStatus.AwaitingApproval, "a live pending approval means the gate is waiting");
        resolved.Should().BeEmpty("a still-pending gate has not resolved");
    }

    [Fact]
    public void Resolved_NotFailed_MarksCompleted()
    {
        var resolved = Set();
        var status = new Dictionary<string, JobStatus> { ["gate"] = JobStatus.AwaitingApproval };

        // Gate was awaiting, now absent from pending, batch not failed ⇒ approved/auto-approved.
        ApprovalGateReconciler.Apply(Set(), previousAwaiting: Set("gate"), batchFailed: false, resolved, status);

        status["gate"].Should().Be(JobStatus.Completed);
        resolved.Should().Contain("gate");
    }

    [Fact]
    public void Resolved_BatchFailed_MarksFailed()
    {
        var resolved = Set();
        var status = new Dictionary<string, JobStatus> { ["gate"] = JobStatus.AwaitingApproval };

        ApprovalGateReconciler.Apply(Set(), previousAwaiting: Set("gate"), batchFailed: true, resolved, status);

        status["gate"].Should().Be(JobStatus.Failed, "a rejected gate / failed batch colours the gate Failed");
        resolved.Should().Contain("gate");
    }

    [Fact]
    public void TerminalIsOneShot_ReappearingPending_DoesNotDragBackToWaiting()
    {
        var resolved = Set("gate");                                            // already resolved
        var status = new Dictionary<string, JobStatus> { ["gate"] = JobStatus.Completed };

        // A stale/duplicate pending entry must NOT re-colour an approved gate.
        ApprovalGateReconciler.Apply(Set("gate"), previousAwaiting: Set(), batchFailed: false, resolved, status);

        status["gate"].Should().Be(JobStatus.Completed, "resolved gates never revert to waiting");
    }

    [Fact]
    public void TerminalIsOneShot_LaterBatchFailure_DoesNotReColourApprovedGate()
    {
        var resolved = Set("gate");                                            // approved earlier
        var status = new Dictionary<string, JobStatus> { ["gate"] = JobStatus.Completed };

        // Batch later fails on a DOWNSTREAM step — the already-approved gate must stay Completed.
        ApprovalGateReconciler.Apply(Set(), previousAwaiting: Set("gate"), batchFailed: true, resolved, status);

        status["gate"].Should().Be(JobStatus.Completed, "an approved gate is not retro-failed by a later step");
    }

    [Fact]
    public void MultipleGates_MixedStates_EachResolvesIndependently()
    {
        var resolved = Set();
        var status = new Dictionary<string, JobStatus>
        {
            ["g1"] = JobStatus.AwaitingApproval,
            ["g2"] = JobStatus.AwaitingApproval,
        };

        // g1 resolves (approved), g2 stays pending.
        ApprovalGateReconciler.Apply(Set("g2"), previousAwaiting: Set("g1", "g2"), batchFailed: false, resolved, status);

        status["g1"].Should().Be(JobStatus.Completed);
        status["g2"].Should().Be(JobStatus.AwaitingApproval);
        resolved.Should().Contain("g1").And.NotContain("g2");
    }

    [Fact]
    public void NoGates_NoMutation()
    {
        var resolved = Set();
        var status = new Dictionary<string, JobStatus>(StringComparer.Ordinal);

        ApprovalGateReconciler.Apply(Set(), previousAwaiting: Set(), batchFailed: false, resolved, status);

        status.Should().BeEmpty();
        resolved.Should().BeEmpty();
    }
}
