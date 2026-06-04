using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Models;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// pure rollup logic for <see cref="RunSummaryViewModel.FromExecutions"/>.
/// No Blazor. Locks the FinalStatus precedence, the Started/Completed aggregation, and the
/// Duration computation the "Recent runs" table depends on.
/// </summary>
public sealed class RunSummaryViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static JobExecution Exec(JobStatus status, DateTimeOffset enqueued, DateTimeOffset? completed = null) => new()
    {
        ExecutionId = Guid.NewGuid().ToString("N"),
        JobName = "j",
        BatchId = "run-1",
        Status = status,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = enqueued,
        CompletedAtUtc = completed,
        AttemptNumber = 1,
        MaxRetries = 0,
        Processed = 0,
        Failed = 0,
    };

    [Fact]
    public void FromExecutions_AllCompleted_RollsUpCompleted_WithMaxCompletedTime()
    {
        var execs = new[]
        {
            Exec(JobStatus.Completed, T0, T0.AddSeconds(30)),
            Exec(JobStatus.Completed, T0.AddSeconds(5), T0.AddSeconds(50)),
        };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs);

        r.FinalStatus.Should().Be(JobStatus.Completed);
        r.StepCount.Should().Be(2);
        r.StartedAtUtc.Should().Be(T0, "earliest EnqueuedAtUtc");
        r.CompletedAtUtc.Should().Be(T0.AddSeconds(50), "latest CompletedAtUtc when every child completed");
        r.Duration.Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public void FromExecutions_AnyFailed_RollsUpFailed_EvenIfOthersCompleted()
    {
        var execs = new[]
        {
            Exec(JobStatus.Completed, T0, T0.AddSeconds(10)),
            Exec(JobStatus.Failed, T0, T0.AddSeconds(20)),
        };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs);

        r.FinalStatus.Should().Be(JobStatus.Failed, "a Failed child dominates the rollup");
    }

    [Fact]
    public void FromExecutions_AnyCancelled_RollsUpFailed()
    {
        var execs = new[]
        {
            Exec(JobStatus.Completed, T0, T0.AddSeconds(10)),
            Exec(JobStatus.Cancelled, T0, T0.AddSeconds(15)),
        };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs);

        r.FinalStatus.Should().Be(JobStatus.Failed,
 "a Cancelled child also rolls up as the Failed (fault) bucket");
    }

    [Fact]
    public void FromExecutions_AnyNonTerminal_RollsUpRunning_NullCompletedAndDuration()
    {
        var execs = new[]
        {
            Exec(JobStatus.Completed, T0, T0.AddSeconds(10)),
            Exec(JobStatus.Running, T0, completed: null),
        };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs);

        r.FinalStatus.Should().Be(JobStatus.Running);
        r.CompletedAtUtc.Should().BeNull("an unfinished child leaves the run without a completion time");
        r.Duration.Should().BeNull("no duration while running");
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Retrying)]
    [InlineData(JobStatus.AwaitingApproval)]
    [InlineData(JobStatus.Cancelling)]
    public void FromExecutions_NonTerminalVariants_RollUpRunning(JobStatus nonTerminal)
    {
        // Failed precedence does NOT apply here (no Failed/Cancelled child) → these all read Running.
        var execs = new[] { Exec(nonTerminal, T0, completed: null) };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs);

        r.FinalStatus.Should().Be(JobStatus.Running,
            $"{nonTerminal} is non-terminal and not a fault → rolls up to Running");
    }

    [Fact]
    public void FromExecutions_FailedPrecedenceWinsOverNonTerminal()
    {
        // A run with BOTH a still-running child AND a failed child reads Failed (fault wins).
        var execs = new[]
        {
            Exec(JobStatus.Running, T0, completed: null),
            Exec(JobStatus.Failed, T0, T0.AddSeconds(5)),
        };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs);

        r.FinalStatus.Should().Be(JobStatus.Failed, "Failed/Cancelled precedence beats non-terminal");
    }

    [Fact]
    public void FromExecutions_EmptySet_Throws()
    {
        Action act = () => RunSummaryViewModel.FromExecutions("run-1", Array.Empty<JobExecution>());
        act.Should().Throw<ArgumentException>("a run summary needs at least one execution");
    }

    [Fact]
    public void FromExecutions_BlankBatchId_Throws()
    {
        var execs = new[] { Exec(JobStatus.Completed, T0, T0.AddSeconds(1)) };
        Action act = () => RunSummaryViewModel.FromExecutions("", execs);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromExecutions_AllJobsCompletedButPendingApproval_RollsUpAwaitingApproval_NotCompleted()
    {
        // A batch PAUSED at an approval gate: every JOB completed, but the gate (no execution row) is
        // still pending. Without the flag this falsely reported "Completed" with a duration.
        var execs = new[] { Exec(JobStatus.Completed, T0, T0.AddSeconds(3)) };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs, hasPendingApproval: true);

        r.FinalStatus.Should().Be(JobStatus.AwaitingApproval, "a gate-paused run is not done");
        r.CompletedAtUtc.Should().BeNull("a run waiting at a gate has no completion time");
        r.Duration.Should().BeNull("no misleading duration for an unfinished run");
    }

    [Fact]
    public void FromExecutions_FailedWithPendingApproval_StillFailed()
    {
        // Failure precedence beats awaiting-approval (a rejected/failed run is terminal, not "waiting").
        var execs = new[]
        {
            Exec(JobStatus.Failed, T0, T0.AddSeconds(2)),
            Exec(JobStatus.Completed, T0.AddSeconds(1), T0.AddSeconds(3)),
        };

        var r = RunSummaryViewModel.FromExecutions("run-1", execs, hasPendingApproval: true);

        r.FinalStatus.Should().Be(JobStatus.Failed);
    }
}
