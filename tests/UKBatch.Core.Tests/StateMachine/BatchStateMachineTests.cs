using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.StateMachine;

/// <summary>
/// Verifies every legal and illegal transition declared in the state-transition matrix.
/// </summary>
public class BatchStateMachineTests
{
    [Theory]
    [InlineData(JobStatus.Pending, JobStatus.Running)]
    [InlineData(JobStatus.Pending, JobStatus.Cancelling)]
    [InlineData(JobStatus.Pending, JobStatus.Failed)]
    [InlineData(JobStatus.Running, JobStatus.Completed)]
    [InlineData(JobStatus.Running, JobStatus.Failed)]
    [InlineData(JobStatus.Running, JobStatus.Retrying)]
    [InlineData(JobStatus.Running, JobStatus.AwaitingApproval)]
    [InlineData(JobStatus.Running, JobStatus.Cancelling)]
    [InlineData(JobStatus.Retrying, JobStatus.Running)]
    [InlineData(JobStatus.Retrying, JobStatus.Cancelling)]
    [InlineData(JobStatus.AwaitingApproval, JobStatus.Running)]
    [InlineData(JobStatus.AwaitingApproval, JobStatus.Failed)]
    [InlineData(JobStatus.AwaitingApproval, JobStatus.Cancelling)]
    [InlineData(JobStatus.Cancelling, JobStatus.Cancelled)]
    public void CanTransition_LegalTransitions_ReturnsTrue(JobStatus from, JobStatus to)
    {
        BatchStateMachine.CanTransition(from, to).Should().BeTrue($"{from} -> {to} should be allowed by the transition matrix");
    }

    [Theory]
    [InlineData(JobStatus.Pending, JobStatus.Pending)]   // self-loop disallowed
    [InlineData(JobStatus.Pending, JobStatus.Completed)] // pre-empt running
    [InlineData(JobStatus.Pending, JobStatus.Cancelled)] // skip Cancelling
    [InlineData(JobStatus.Running, JobStatus.Cancelled)] // direct to terminal cancel forbidden
    [InlineData(JobStatus.Running, JobStatus.Pending)]   // backwards
    [InlineData(JobStatus.AwaitingApproval, JobStatus.Cancelled)] // B2 — direct edge removed
    [InlineData(JobStatus.AwaitingApproval, JobStatus.Completed)] // approved must go through Running
    [InlineData(JobStatus.Retrying, JobStatus.Completed)] // retry must re-enter Running
    [InlineData(JobStatus.Retrying, JobStatus.Failed)]    // retry's only exits are Running and Cancelling
    [InlineData(JobStatus.Completed, JobStatus.Failed)]   // terminal -> no outgoing
    [InlineData(JobStatus.Failed, JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled, JobStatus.Pending)]
    [InlineData(JobStatus.Scheduled, JobStatus.Running)]  // Scheduled has no outgoing in v0.1
    [InlineData(JobStatus.Scheduled, JobStatus.Pending)]
    public void CanTransition_IllegalTransitions_ReturnsFalse(JobStatus from, JobStatus to)
    {
        BatchStateMachine.CanTransition(from, to).Should().BeFalse($"{from} -> {to} should be forbidden");
    }

    [Fact]
    public void Validate_LegalTransition_DoesNotThrow()
    {
        Action act = () => BatchStateMachine.Validate(JobStatus.Pending, JobStatus.Running);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_IllegalTransition_ThrowsInvalidJobTransitionException()
    {
        Action act = () => BatchStateMachine.Validate(JobStatus.Pending, JobStatus.Completed);
        var ex = act.Should().Throw<InvalidJobTransitionException>().Which;
        ex.From.Should().Be(JobStatus.Pending);
        ex.To.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void Validate_IllegalTransition_ExceptionIsInvalidOperationException()
    {
        // Storage adapters rely on the frozen contract that stores throw InvalidOperationException
        // on illegal transitions (see IJobExecutionWriter.UpdateStatusAsync xmldoc).
        Action act = () => BatchStateMachine.Validate(JobStatus.Completed, JobStatus.Pending);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void IsTerminal_TerminalStates_ReturnsTrue(JobStatus status)
    {
        BatchStateMachine.IsTerminal(status).Should().BeTrue();
    }

    [Theory]
    [InlineData(JobStatus.Scheduled)]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Retrying)]
    [InlineData(JobStatus.AwaitingApproval)]
    [InlineData(JobStatus.Cancelling)]
    public void IsTerminal_NonTerminalStates_ReturnsFalse(JobStatus status)
    {
        BatchStateMachine.IsTerminal(status).Should().BeFalse();
    }

    [Fact]
    public void Matrix_NoSelfLoopsAllowed()
    {
        // invariant: self-loops are disallowed (idempotent updates must be explicit at higher layers).
        foreach (JobStatus s in Enum.GetValues<JobStatus>())
        {
            BatchStateMachine.CanTransition(s, s).Should().BeFalse($"{s} -> {s} self-loop should be disallowed");
        }
    }

    [Fact]
    public void Matrix_TerminalStatesHaveNoOutgoingEdges()
    {
        foreach (var terminal in new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled })
        {
            foreach (JobStatus to in Enum.GetValues<JobStatus>())
            {
                BatchStateMachine.CanTransition(terminal, to).Should().BeFalse(
                    $"{terminal} is terminal and must have no outgoing edge to {to}");
            }
        }
    }

    [Fact]
    public void Matrix_OnlyEdgeIntoCancelledIsFromCancelling()
    {
        // invariant: all cancellations flow through Cancelling.
        foreach (JobStatus from in Enum.GetValues<JobStatus>())
        {
            var canReachCancelled = BatchStateMachine.CanTransition(from, JobStatus.Cancelled);
            if (from == JobStatus.Cancelling)
            {
                canReachCancelled.Should().BeTrue();
            }
            else
            {
                canReachCancelled.Should().BeFalse($"Direct edge from {from} to Cancelled forbidden (B2)");
            }
        }
    }

    [Fact]
    public void Matrix_ScheduledHasNoOutgoingEdges()
    {
        // .1.2 — Scheduled reserved for v0.2 durable scheduler; no outgoing in v0.1.
        foreach (JobStatus to in Enum.GetValues<JobStatus>())
        {
            BatchStateMachine.CanTransition(JobStatus.Scheduled, to).Should().BeFalse(
                $"Scheduled -> {to} forbidden (D0.1.2)");
        }
    }
}
