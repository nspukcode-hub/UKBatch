using UKBatch.Abstractions.Models;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// A one-row-per-run summary. Built either from the authoritative persisted <see cref="BatchRun"/>
/// (<see cref="FromBatchRun"/> — the preferred source) or, as a transitional fallback, by rolling up the
/// executions that share one <see cref="JobExecution.BatchId"/> (<see cref="FromExecutions"/>). Drives the
/// "Recent runs" table on <c>Batches/Detail</c> and the Runs table on the Executions page — one row per
/// run instead of one row per execution.
/// </summary>
/// <remarks>
/// <para><b>Preferred source — the run-store.</b> <see cref="FromBatchRun"/> reads the run's own recorded
/// terminal <see cref="BatchRun.Status"/> and definition <see cref="BatchRun.StepCount"/>, so neither the
/// execution-roll-up's undercount nor its gate-failed-reads-Completed blind spot applies: a gate-failed run
/// shows Failed, a running run (Status null) shows Running, and the counts come straight off the row.</para>
/// <para><b>Transitional fallback — execution roll-up.</b> <see cref="FromExecutions"/> remains for the
/// degraded path. Its source query
/// (<c>IUKBatchClient.QueryExecutionsAsync(BatchDefinitionId, Limit=50)</c>) caps EXECUTIONS, not runs, so a
/// many-step run could undercount its <see cref="StepCount"/>, and a run paused at an approval gate (no
/// execution row) needs the <c>hasPendingApproval</c> hint to avoid falsely reading Completed. The
/// run-store path has none of these caveats.</para>
/// </remarks>
public sealed record class RunSummaryViewModel
{
    /// <summary>The batch RUN id (UUIDv7), one per <c>TriggerBatchAsync</c> invocation.</summary>
    public required string BatchId { get; init; }

    /// <summary>
    /// Run status. From <see cref="FromBatchRun"/> this is the run's own recorded terminal status (or
    /// <see cref="JobStatus.Running"/> while in progress). From <see cref="FromExecutions"/> it is the
    /// roll-up: <see cref="JobStatus.Failed"/> if any child Failed/Cancelled; else
    /// <see cref="JobStatus.Running"/> if any child is non-terminal; else <see cref="JobStatus.Completed"/>.
    /// </summary>
    public required JobStatus FinalStatus { get; init; }

    /// <summary>
    /// Step count. From <see cref="FromBatchRun"/> this is the definition's step count (a planning number,
    /// fixed at create time). From <see cref="FromExecutions"/> it is the number of executions observed (capped
    /// by the source query).
    /// </summary>
    public required int StepCount { get; init; }

    /// <summary>UTC start time of the run.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC completion time of the run; <c>null</c> while still in progress.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Total execution rows of the run; <c>0</c> when unknown (the <see cref="FromExecutions"/> path leaves it 0).</summary>
    public int Total { get; init; }

    /// <summary>Executions that finished succeeded; <c>0</c> when unknown.</summary>
    public int Succeeded { get; init; }

    /// <summary>Executions that finished failed; <c>0</c> when unknown.</summary>
    public int Failed { get; init; }

    /// <summary>Executions that finished cancelled; <c>0</c> when unknown.</summary>
    public int Cancelled { get; init; }

    /// <summary>Wall-clock duration (<see cref="CompletedAtUtc"/> − <see cref="StartedAtUtc"/>), or <c>null</c> while running.</summary>
    public TimeSpan? Duration => CompletedAtUtc is { } end ? end - StartedAtUtc : null;

    /// <summary>
    /// Builds a summary directly from a persisted <see cref="BatchRun"/> — the authoritative source. The
    /// run's own <see cref="BatchRun.Status"/> drives <see cref="FinalStatus"/> (a running run, Status null,
    /// reads <see cref="JobStatus.Running"/>), and <see cref="StepCount"/> is the definition step count, so
    /// neither the undercount nor the gate-failed-reads-Completed problems of the execution roll-up apply.
    /// </summary>
    public static RunSummaryViewModel FromBatchRun(BatchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunSummaryViewModel
        {
            BatchId = run.BatchId,
            FinalStatus = run.Status ?? JobStatus.Running,   // null == in progress
            StepCount = run.StepCount,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            Total = run.Total,
            Succeeded = run.Succeeded,
            Failed = run.Failed,
            Cancelled = run.Cancelled,
        };
    }

    /// <summary>
    /// Rolls a non-empty set of executions sharing <paramref name="batchId"/> into one summary.
    /// </summary>
    /// <param name="batchId">The run id all <paramref name="executions"/> belong to.</param>
    /// <param name="executions">The run's executions (must be non-empty).</param>
    /// <param name="hasPendingApproval">
    /// <c>true</c> when the run has a live approval gate awaiting decision. Approval gates are NOT jobs
    /// (no <see cref="JobExecution"/> row), so a batch PAUSED at a gate would otherwise roll up to
    /// <see cref="JobStatus.Completed"/> once its jobs finish — falsely reporting a still-running batch as
    /// done. When set (and the run hasn't failed) the status is <see cref="JobStatus.AwaitingApproval"/>
    /// and <see cref="CompletedAtUtc"/> stays <c>null</c>.
    /// </param>
    public static RunSummaryViewModel FromExecutions(
        string batchId, IReadOnlyList<JobExecution> executions, bool hasPendingApproval = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentNullException.ThrowIfNull(executions);
        if (executions.Count == 0)
            throw new ArgumentException("Cannot summarise an empty execution set.", nameof(executions));

        var anyFailedOrCancelled = false;
        var anyNonTerminal = false;
        var startedAtUtc = DateTimeOffset.MaxValue;
        DateTimeOffset maxCompletedAtUtc = DateTimeOffset.MinValue;
        var allCompletedTimesPresent = true;

        foreach (var e in executions)
        {
            if (e.Status is JobStatus.Failed or JobStatus.Cancelled)
                anyFailedOrCancelled = true;
            else if (e.Status is not JobStatus.Completed)
                anyNonTerminal = true; // Pending / Running / Retrying / AwaitingApproval / Cancelling

            if (e.EnqueuedAtUtc < startedAtUtc)
                startedAtUtc = e.EnqueuedAtUtc;

            if (e.CompletedAtUtc is { } c)
            {
                if (c > maxCompletedAtUtc) maxCompletedAtUtc = c;
            }
            else
            {
                allCompletedTimesPresent = false;
            }
        }

        var finalStatus = anyFailedOrCancelled
            ? JobStatus.Failed
            : anyNonTerminal ? JobStatus.Running
            : hasPendingApproval ? JobStatus.AwaitingApproval   // paused at an approval gate (no job row)
            : JobStatus.Completed;

        // CompletedAtUtc is only meaningful when the whole run is terminal (every child has a completion
        // time) AND it isn't paused at an approval gate. A non-terminal or gate-paused run reads as "still
        // in progress" (null) so no misleading duration shows for a batch that hasn't finished.
        DateTimeOffset? completedAtUtc =
            allCompletedTimesPresent && !hasPendingApproval ? maxCompletedAtUtc : null;

        return new RunSummaryViewModel
        {
            BatchId = batchId,
            FinalStatus = finalStatus,
            StepCount = executions.Count,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
        };
    }
}
