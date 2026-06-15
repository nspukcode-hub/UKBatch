using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Pluggable persistent store for batch RUN records — one row per <c>IJobRunner.TriggerBatchAsync</c>
/// invocation. Enables run-grouped views, run-paginated history, and the durable terminal status a
/// roll-up over execution rows cannot supply (a gate-failed run leaves no execution row).
/// </summary>
/// <remarks>
/// <para>The default in-memory implementation (<c>InMemoryBatchRunStore</c>, shipped in
/// <c>UKBatch.Core</c>) keeps the in-process deployment fully working. The EF adapter replaces it so
/// runs survive restarts. Implementations MUST be thread-safe.</para>
/// <para>There is intentionally NO change feed / watch surface here: run records are written once at
/// create and once at completion, and the dashboard reads them on navigate. Live run-status flips reach
/// the dashboard through the existing SignalR batch-completion channel, which is independent of this
/// store.</para>
/// <para><b>Not durable workflow resume.</b> A run row left with <see cref="BatchRun.Status"/> null after
/// a host crash is an in-progress record that will not auto-resume (durable resume is a later release);
/// it is honest history, not a resumable cursor.</para>
/// </remarks>
public interface IBatchRunStore
{
    /// <summary>
    /// Inserts a new run row in its in-progress state (<see cref="BatchRun.Status"/> null,
    /// <see cref="BatchRun.CompletedAtUtc"/> null, all four execution counters 0). Throws
    /// <see cref="InvalidOperationException"/> if a row with that <see cref="BatchRun.BatchId"/> already
    /// exists (UUIDv7 collision is astronomically unlikely; handled defensively).
    /// </summary>
    Task CreateAsync(BatchRun run, CancellationToken cancellationToken);

    /// <summary>
    /// Stamps a run terminal: sets <see cref="BatchRun.Status"/> to <paramref name="terminalStatus"/>
    /// (one of Completed / Failed / Cancelled), records the executed counters from
    /// <paramref name="counts"/>, and sets <see cref="BatchRun.CompletedAtUtc"/> to
    /// <paramref name="completedAtUtc"/>. A no-op when the run id is absent (the create write may have
    /// failed; completion must not throw on a missing row and crash the fire-and-forget closure).
    /// </summary>
    /// <remarks>
    /// Unconditional last-write-wins; this method does NOT dedupe. Today the runtime's completion path
    /// runs exactly once per run, so a single completer in practice. A future retry/resume layer that may
    /// complete a run id more than once owns its own idempotency (the same way the SignalR hub dedupes
    /// completion signals through its own LRU).
    /// </remarks>
    Task CompleteAsync(
        string batchId,
        JobStatus terminalStatus,
        BatchRunCounts counts,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Returns the run by id, or <c>null</c> if absent.</summary>
    Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a page of runs matching <paramref name="query"/>, ordered by
    /// <see cref="BatchRun.StartedAtUtc"/> (descending by default) with the run id as a stable tiebreak.
    /// </summary>
    /// <remarks>
    /// The run-id tiebreak relies on the id being lowercase hex (UUIDv7 "N" format), so an ordinal
    /// (in-memory) comparison and a binary/locale database collation agree on page order for runs sharing
    /// an identical <see cref="BatchRun.StartedAtUtc"/>. A future non-hex run id would break that
    /// cross-store page-order parity.
    /// </remarks>
    Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken);

    /// <summary>Returns the total count of runs matching the filter (ignores <see cref="BatchRunQuery.Offset"/> / <see cref="BatchRunQuery.Limit"/>) so pagers can compute page bounds.</summary>
    Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken);
}
