namespace UKBatch.Abstractions.Models;

/// <summary>
/// Immutable record of one batch run — a single <c>IJobRunner.TriggerBatchAsync</c> invocation —
/// as persisted by <see cref="Storage.IBatchRunStore"/>. One <see cref="Batches.BatchDefinition"/> has many
/// runs; one run has many <see cref="JobExecution"/> rows (joined by
/// <see cref="JobExecution.BatchId"/> == <see cref="BatchId"/>).
/// </summary>
/// <remarks>
/// <para>The run row is the authoritative source for a run's terminal status. A roll-up over the run's
/// <see cref="JobExecution"/> rows is blind to an approval-gate failure (a rejected / timed-out-Fail
/// gate ends the run yet leaves no execution row), so a run paused-then-failed at a gate would falsely
/// roll up to <see cref="JobStatus.Completed"/>. The runtime stamps the genuine terminal status here
/// from its own verdict.</para>
/// <para><b>Counters semantics.</b> <see cref="StepCount"/> is the DEFINITION's step count, fixed at
/// create time; it never equals the executed-row total, so it cannot be confused with
/// <see cref="Total"/>. The four execution counters (<see cref="Total"/>, <see cref="Succeeded"/>,
/// <see cref="Failed"/>, <see cref="Cancelled"/>) are derived from one query over the run's executions
/// at completion. <see cref="Total"/> counts every execution row of the run, including cross-service
/// shadow rows.</para>
/// <para><b>Adapter contract:</b> persistent store adapters (EF Core, future Redis) MUST round-trip
/// every field verbatim. The store is write-then-read-back only — there is no live change feed for
/// runs (the dashboard polls on navigate; live run-status flips still arrive over the existing
/// SignalR batch-completion path, independent of this store).</para>
/// <para><b>Forward compatibility:</b> later releases extend this record with OPTIONAL
/// (non-<c>required</c>, defaulted) fields only — the durable-resume cursor
/// (<see cref="CurrentStepIndex"/>) arrived this way, so existing producers keep compiling unchanged.</para>
/// </remarks>
public sealed record class BatchRun
{
    /// <summary>The batch RUN id (UUIDv7, "N" format) — the primary key, one per <c>TriggerBatchAsync</c>.</summary>
    public required string BatchId { get; init; }

    /// <summary>The DEFINITION id this run was launched from (stable per <c>AddBatch</c> or per Store row).</summary>
    public required string BatchDefinitionId { get; init; }

    /// <summary>Display name of the definition, captured at create time so a later rename does not retro-rewrite history.</summary>
    public required string BatchName { get; init; }

    /// <summary>
    /// Terminal status of the run, or <c>null</c> while the run is still in progress. A non-null value is
    /// always one of <see cref="JobStatus.Completed"/>, <see cref="JobStatus.Failed"/>, or
    /// <see cref="JobStatus.Cancelled"/> — set once, in the completion path, from the runtime's own verdict.
    /// </summary>
    public JobStatus? Status { get; init; }

    /// <summary>Identity that triggered the run; <c>null</c> when unattributed.</summary>
    public string? TriggeredBy { get; init; }

    /// <summary>UTC time the run was created (the trigger thread, before the fire-and-forget executor starts).</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC time the run reached a terminal status; <c>null</c> while still in progress.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// Resume cursor: the zero-based index of the NEXT step to run, into the ordered step sequence
    /// (the same sequence the executor walks — Job / ParallelGroup / ApprovalGate steps in
    /// <see cref="Batches.BatchStep.Order"/> order). Advances by one each time a step completes
    /// successfully. <c>null</c> means "no cursor recorded" — a run created before durable resume
    /// existed, or an in-memory run (cursors are only meaningful on a persistent store). A value equal
    /// to the run's ordered-step count means every step finished.
    /// </summary>
    /// <remarks>
    /// This is a RUN-scoped progress marker against the run's creation-time topology, NOT a definition
    /// pointer. It is additive (non-<c>required</c>, default <c>null</c>): producers that never set it
    /// keep compiling, and a roll-up over <see cref="JobExecution"/> rows is unaffected.
    /// </remarks>
    public int? CurrentStepIndex { get; init; }

    /// <summary>
    /// Number of steps in the definition (Job steps + nested ParallelGroup children + ApprovalGate steps +
    /// OnFailureSteps), fixed at create time. A planning/topology number — NOT an executed-row count.
    /// </summary>
    public required int StepCount { get; init; }

    /// <summary>Total execution rows observed for this run at completion (includes cross-service shadow rows); <c>0</c> while running.</summary>
    public required int Total { get; init; }

    /// <summary>Executions that finished <see cref="JobStatus.Completed"/>; <c>0</c> while running.</summary>
    public required int Succeeded { get; init; }

    /// <summary>Executions that finished <see cref="JobStatus.Failed"/>; <c>0</c> while running.</summary>
    public required int Failed { get; init; }

    /// <summary>Executions that finished <see cref="JobStatus.Cancelled"/>; <c>0</c> while running.</summary>
    public required int Cancelled { get; init; }

    /// <summary>
    /// Run-scoped state carried across a durable resume: the batch-initial parameters and the
    /// accumulated step outputs, held under reserved <c>ukbatch.*</c> keys. <c>null</c> for runs created
    /// before this field existed, in-memory runs, or stores that do not persist it. Additive
    /// (non-<c>required</c>, default <c>null</c>), so existing producers keep compiling. Values are
    /// JSON-serializable; an adapter MUST round-trip this field verbatim.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ForwardedState { get; init; }
}

/// <summary>
/// The four executed-execution counts for a finished run, computed once over the run's
/// <see cref="JobExecution"/> rows and passed to <see cref="Storage.IBatchRunStore.CompleteAsync"/>.
/// </summary>
/// <param name="Total">Every execution row of the run (includes cross-service shadow rows).</param>
/// <param name="Succeeded">Rows in <see cref="JobStatus.Completed"/>.</param>
/// <param name="Failed">Rows in <see cref="JobStatus.Failed"/>.</param>
/// <param name="Cancelled">Rows in <see cref="JobStatus.Cancelled"/>.</param>
public readonly record struct BatchRunCounts(int Total, int Succeeded, int Failed, int Cancelled);
