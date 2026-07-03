using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Runtime;

/// <summary>
/// Reads the recorded decision of an approval gate for a (run, step) pair so a resumed run can honor a
/// gate that was already decided before a crash instead of opening a fresh gate and blocking on a human
/// again. A thin read-only seam over the existing <see cref="IApprovalGateService.ListForBatchAsync"/>:
/// it adds no store method and is consulted ONLY on the resume path (<c>null</c> on the trigger path,
/// where there is no prior decision yet, keeps the executor byte-for-byte).
/// </summary>
internal interface IResumeGateProbe
{
    /// <summary>
    /// Returns the latest DECIDED outcome of the approval gate guarding <paramref name="stepId"/> in run
    /// <paramref name="batchId"/>, or <c>null</c> when the step has no decided gate record (the first
    /// pass, or only a pending record). The correlation key is (run, step) because no gate id is stable
    /// across attempts — each await mints a new approval id.
    /// </summary>
    Task<ApprovalRecordOutcome?> TryGetDecidedOutcomeAsync(string batchId, string stepId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the approval id of an existing PENDING gate for (<paramref name="batchId"/>,
    /// <paramref name="stepId"/>) — a gate created before a crash/shutdown that was never decided — so a
    /// resumed run RE-ATTACHES to it instead of minting a second pending gate (which would show the
    /// operator two identical approvals). Returns <c>null</c> when no pending gate exists, in which case
    /// the resumed run opens a fresh gate exactly as the first attempt did. The latest pending record
    /// wins (by the store's recency ordering).
    /// </summary>
    Task<string?> TryGetPendingApprovalIdAsync(string batchId, string stepId, CancellationToken cancellationToken);
}

/// <summary>
/// A proven-completed cross-service shadow row: its terminal <see cref="JobStatus"/> (always
/// <see cref="JobStatus.Completed"/>) and the outputs it persisted. Carrying both lets a resumed run
/// skip re-dispatch AND forward the outputs the skipped step produced — recovered from the durable
/// shadow row when the crash landed before the run's forwarded state was saved.
/// </summary>
internal readonly record struct ResumeShadowCompletion(
    JobStatus Status,
    IReadOnlyDictionary<string, object?>? Outputs);

/// <summary>
/// Reports whether a cross-service shadow execution row for a (run, step) pair PROVES the step already
/// finished before a crash, so a resumed run can skip a step it has already completed instead of
/// re-dispatching it. A thin read-only seam over the existing <see cref="IJobExecutionReader.QueryAsync"/>:
/// it adds no query surface and is consulted ONLY on the resume path (<c>true</c> for "definitely done"
/// requires a <see cref="JobStatus.Completed"/> row; <c>null</c> on the trigger path, where there is no
/// prior row, keeps the cross-service dispatch byte-for-byte).
/// </summary>
internal interface IResumeShadowProbe
{
    /// <summary>
    /// Returns a <see cref="ResumeShadowCompletion"/> (status always <see cref="JobStatus.Completed"/>, plus
    /// the outputs that row persisted) if the cross-service step <paramref name="stepId"/> in run
    /// <paramref name="batchId"/> has a shadow row that proves it definitely finished before the crash, or
    /// <c>null</c> otherwise. ONLY a <see cref="JobStatus.Completed"/> row proves completion; any other
    /// state — a non-terminal <see cref="JobStatus.Running"/> row, a row tombstoned to
    /// <see cref="JobStatus.Failed"/> by the orphan reaper, a remote <see cref="JobStatus.Failed"/> /
    /// <see cref="JobStatus.Cancelled"/>, or no row at all — does NOT prove completion, so it maps to
    /// <c>null</c> and resume re-dispatches the step (the at-least-once replay). The symmetric counterpart
    /// of <see cref="IResumeGateProbe"/>, which re-opens a non-decided gate rather than fail-routing it.
    /// </summary>
    Task<ResumeShadowCompletion?> TryGetCompletedStatusAsync(string batchId, string stepId, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IResumeGateProbe"/> over <see cref="IApprovalGateService.ListForBatchAsync"/>.
/// </summary>
internal sealed class ResumeGateProbe : IResumeGateProbe
{
    private readonly IApprovalGateService _gateService;

    public ResumeGateProbe(IApprovalGateService gateService)
    {
        ArgumentNullException.ThrowIfNull(gateService);
        _gateService = gateService;
    }

    public async Task<ApprovalRecordOutcome?> TryGetDecidedOutcomeAsync(string batchId, string stepId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentException.ThrowIfNullOrEmpty(stepId);

        var gates = await _gateService.ListForBatchAsync(batchId, cancellationToken).ConfigureAwait(false);

        // Latest DECIDED gate for this step wins. A re-run could leave more than one decided record for the
        // same step; the decision TIME (DecidedAtUtc) orders them. The approval id is only a tiebreak — used
        // when two records share a DecidedAtUtc, or when a record predates this field and carries no
        // timestamp (id is UUIDv7, time-ordered, so the highest id is the most recent decision).
        ApprovalGateView? latest = null;
        foreach (var gate in gates)
        {
            if (gate.Status != ApprovalRecordStatus.Decided
                || !string.Equals(gate.BatchStepId, stepId, StringComparison.Ordinal))
            {
                continue;
            }
            if (latest is null || IsMoreRecent(gate, latest))
            {
                latest = gate;
            }
        }

        return latest?.Outcome;
    }

    public async Task<string?> TryGetPendingApprovalIdAsync(string batchId, string stepId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentException.ThrowIfNullOrEmpty(stepId);

        var gates = await _gateService.ListForBatchAsync(batchId, cancellationToken).ConfigureAwait(false);

        // The latest PENDING gate for this step wins. A pending record has no DecidedAtUtc, so recency
        // falls back to the UUIDv7 ApprovalId (time-ordered, so the highest id is the most recently
        // created gate). ListForBatchAsync returns gates already ordered by PendingSinceUtc then
        // ApprovalId, but we select explicitly so the contract does not silently depend on that order.
        ApprovalGateView? latest = null;
        foreach (var gate in gates)
        {
            if (gate.Status != ApprovalRecordStatus.Pending
                || !string.Equals(gate.BatchStepId, stepId, StringComparison.Ordinal))
            {
                continue;
            }
            if (latest is null || string.CompareOrdinal(gate.ApprovalId, latest.ApprovalId) > 0)
            {
                latest = gate;
            }
        }

        return latest?.ApprovalId;
    }

    /// <summary>
    /// Orders two decided gate records by decision recency: later <see cref="ApprovalGateView.DecidedAtUtc"/>
    /// wins; a record with a timestamp beats one without (a missing timestamp is an old/incomplete record);
    /// equal-or-both-missing timestamps fall back to the higher UUIDv7 <see cref="ApprovalGateView.ApprovalId"/>.
    /// </summary>
    private static bool IsMoreRecent(ApprovalGateView candidate, ApprovalGateView current)
    {
        if (candidate.DecidedAtUtc is { } cAt && current.DecidedAtUtc is { } curAt)
        {
            return cAt != curAt
                ? cAt > curAt
                : string.CompareOrdinal(candidate.ApprovalId, current.ApprovalId) > 0;
        }
        if (candidate.DecidedAtUtc is not null)
        {
            return true;   // candidate is timestamped, current is not
        }
        if (current.DecidedAtUtc is not null)
        {
            return false;  // current is timestamped, candidate is not
        }
        return string.CompareOrdinal(candidate.ApprovalId, current.ApprovalId) > 0;
    }
}

/// <summary>
/// Default <see cref="IResumeShadowProbe"/> over <see cref="IJobExecutionReader.QueryAsync"/>. Reads only
/// (<see cref="IJobExecutionReader"/>, not the full <see cref="IJobStore"/>) — it never writes a row.
/// </summary>
internal sealed class ResumeShadowProbe : IResumeShadowProbe
{
    private readonly IJobExecutionReader _reader;

    public ResumeShadowProbe(IJobExecutionReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async Task<ResumeShadowCompletion?> TryGetCompletedStatusAsync(string batchId, string stepId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentException.ThrowIfNullOrEmpty(stepId);

        // JobQuery has a BatchId filter but no BatchStepId filter, so the step match is an in-memory
        // post-filter — the non-invasive choice (no query surface change). The set per run is small.
        var rows = await _reader.QueryAsync(
            new JobQuery { BatchId = batchId, Limit = int.MaxValue, Offset = 0 }, cancellationToken).ConfigureAwait(false);

        // Only a Completed row proves the cross-service step finished. A reaper-tombstoned Failed row, a
        // remote Failed/Cancelled, or a still-Running orphan are all ambiguous or retryable, so they are
        // ignored here and resume re-dispatches (at-least-once). This mirrors the reaper-orphan gate path,
        // which re-opens an Interrupted gate rather than fail-routing it. If ANY attempt completed, the step
        // is done — so the first Completed row found is decisive (no need to pick a "latest").
        foreach (var row in rows)
        {
            if (string.Equals(row.BatchStepId, stepId, StringComparison.Ordinal)
                && row.Status == JobStatus.Completed)
            {
                // Surface the row's persisted outputs so the skip path forwards them (durable on EF; read
                // back as JsonElement values, resolved downstream by the JSON-aware JobParameters readers).
                return new ResumeShadowCompletion(JobStatus.Completed, row.Outputs);
            }
        }

        return null;
    }
}
