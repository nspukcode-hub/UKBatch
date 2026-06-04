namespace UKBatch.Abstractions.Models;

/// <summary>
/// The authoritative <see cref="JobStatus"/> transition matrix and terminal-state predicate —
/// the behavioral contract EVERY <c>IJobStore</c> adapter (in-memory, EF Core, Redis, RabbitMQ)
/// MUST honor before mutating a stored status. Public in Abstractions so adapters validate
/// transitions WITHOUT friend access to Core.
/// </summary>
/// <remarks>
/// <para>Core's internal <c>BatchStateMachine</c> DELEGATES to this type (single source of truth) and
/// adds only its richer <c>InvalidJobTransitionException</c> wrapper.</para>
/// <para>Key invariants:
/// <list type="bullet">
///   <item><c>Scheduled</c> has NO outgoing transitions (v0.1 forward-compat reserved).</item>
///   <item><c>Completed</c> / <c>Failed</c> / <c>Cancelled</c> are terminal.</item>
///   <item>All cancellations flow through <c>Cancelling</c>; no direct edge to <c>Cancelled</c> from any other state.</item>
///   <item>Self-loops are disallowed (idempotent updates must be explicit at higher layers).</item>
///   <item><c>Pending -&gt; Failed</c> allowed for validation short-circuit.</item>
/// </list></para>
/// </remarks>
public static class JobStatusTransitions
{
    private static readonly bool[,] Allowed = BuildMatrix();

    /// <summary>O(1): <c>true</c> if <paramref name="from"/> may legally transition to <paramref name="to"/>.</summary>
    public static bool CanTransition(JobStatus from, JobStatus to) => Allowed[(int)from, (int)to];

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the transition is illegal; returns silently otherwise.
    /// This is the exact exception type the frozen <see cref="Storage.IJobExecutionWriter.UpdateStatusAsync"/>
    /// contract promises, so adapters need no Core-internal exception.
    /// </summary>
    public static void Validate(JobStatus from, JobStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Illegal job status transition: {from} -> {to}.");
        }
    }

    /// <summary><c>true</c> iff <paramref name="status"/> is terminal (Completed / Failed / Cancelled).</summary>
    public static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    private static bool[,] BuildMatrix()
    {
        var n = Enum.GetValues<JobStatus>().Length;
        var m = new bool[n, n];

        // Pending -> Running, Cancelling, Failed (validation short-circuit)
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

        // AwaitingApproval -> Running (approved), Failed (rejected / timeout-fail),
        // Cancelling (manual / host shutdown). NO direct -> Cancelled edge.
        m[(int)JobStatus.AwaitingApproval, (int)JobStatus.Running] = true;
        m[(int)JobStatus.AwaitingApproval, (int)JobStatus.Failed] = true;
        m[(int)JobStatus.AwaitingApproval, (int)JobStatus.Cancelling] = true;

        // Cancelling -> Cancelled (the ONE-AND-ONLY edge into Cancelled).
        m[(int)JobStatus.Cancelling, (int)JobStatus.Cancelled] = true;

        return m;
    }
}
