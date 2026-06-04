using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.StateMachine;

/// <summary>
/// Reflection-light test asserting the runtime matrix matches the canonical transition table
/// byte-for-byte — catches matrix drift on every CI run. Covers all 9 x 9 = 81 transition pairs.
/// </summary>
public class BatchStateMachineMatrixVsSpecTests
{
    // The canonical transition matrix as a 9x9 bool array. Indexed by (int)JobStatus.
    // Row = from, Col = to. true = allowed.
    private static readonly bool[,] SpecMatrix = Build();

    private static bool[,] Build()
    {
        var n = Enum.GetValues<JobStatus>().Length;
        var m = new bool[n, n];

        // Pending -> Running, Cancelling, Failed
        m[(int)JobStatus.Pending, (int)JobStatus.Running] = true;
        m[(int)JobStatus.Pending, (int)JobStatus.Cancelling] = true;
        m[(int)JobStatus.Pending, (int)JobStatus.Failed] = true;

        // Running -> Completed, Failed, Retrying, AwaitingApproval, Cancelling
        m[(int)JobStatus.Running, (int)JobStatus.Completed] = true;
        m[(int)JobStatus.Running, (int)JobStatus.Failed] = true;
        m[(int)JobStatus.Running, (int)JobStatus.Retrying] = true;
        m[(int)JobStatus.Running, (int)JobStatus.AwaitingApproval] = true;
        m[(int)JobStatus.Running, (int)JobStatus.Cancelling] = true;

        // Retrying -> Running, Cancelling
        m[(int)JobStatus.Retrying, (int)JobStatus.Running] = true;
        m[(int)JobStatus.Retrying, (int)JobStatus.Cancelling] = true;

        // AwaitingApproval -> Running, Failed, Cancelling
        m[(int)JobStatus.AwaitingApproval, (int)JobStatus.Running] = true;
        m[(int)JobStatus.AwaitingApproval, (int)JobStatus.Failed] = true;
        m[(int)JobStatus.AwaitingApproval, (int)JobStatus.Cancelling] = true;

        // Cancelling -> Cancelled
        m[(int)JobStatus.Cancelling, (int)JobStatus.Cancelled] = true;

        // All others false by default (Scheduled / Completed / Failed / Cancelled have no outgoing).

        return m;
    }

    public static IEnumerable<object[]> AllPairs()
    {
        foreach (JobStatus from in Enum.GetValues<JobStatus>())
        {
            foreach (JobStatus to in Enum.GetValues<JobStatus>())
            {
                yield return new object[] { from, to };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Matrix_MatchesSpec_ByteForByte(JobStatus from, JobStatus to)
    {
        var expected = SpecMatrix[(int)from, (int)to];
        var actual = BatchStateMachine.CanTransition(from, to);
        actual.Should().Be(expected, $"transition {from} -> {to} must match the canonical transition table");
    }
}
