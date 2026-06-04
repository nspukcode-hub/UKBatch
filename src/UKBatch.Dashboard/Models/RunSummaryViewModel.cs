using UKBatch.Abstractions.Models;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// A per-RUN rollup of the executions that share one <see cref="JobExecution.BatchId"/>.
/// Drives the "Recent runs" table on <c>Batches/Detail</c> — one row per run instead of one row per
/// execution (the page ships a single per-run table, not a per-execution table).
/// </summary>
/// <remarks>
/// <para><b>v0.1 cap (do NOT fix here):</b> the source query
/// (<c>IUKBatchClient.QueryExecutionsAsync(BatchDefinitionId, Limit=50)</c>) caps EXECUTIONS, not
/// runs, so a many-step run could undercount its <see cref="StepCount"/>. The demo runs ≤3 steps;
/// a proper run-store is v0.2.</para>
/// </remarks>
public sealed record class RunSummaryViewModel
{
    /// <summary>The batch RUN id (UUIDv7), one per <c>TriggerBatchAsync</c> invocation.</summary>
    public required string BatchId { get; init; }

    /// <summary>
    /// Rolled-up status across the run's executions: <see cref="JobStatus.Failed"/> if any child
    /// Failed/Cancelled; else <see cref="JobStatus.Running"/> if any child is non-terminal; else
    /// <see cref="JobStatus.Completed"/>.
    /// </summary>
    public required JobStatus FinalStatus { get; init; }

    /// <summary>Number of executions observed for this run (capped by the source query — see remarks).</summary>
    public required int StepCount { get; init; }

    /// <summary>Earliest <see cref="JobExecution.EnqueuedAtUtc"/> across the run's executions.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// Latest <see cref="JobExecution.CompletedAtUtc"/> when EVERY execution has completed;
    /// <c>null</c> while any execution is still non-terminal.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Wall-clock duration (<see cref="CompletedAtUtc"/> − <see cref="StartedAtUtc"/>), or <c>null</c> while running.</summary>
    public TimeSpan? Duration => CompletedAtUtc is { } end ? end - StartedAtUtc : null;

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
