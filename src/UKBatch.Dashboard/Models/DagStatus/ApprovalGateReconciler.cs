using UKBatch.Abstractions.Models;

namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// Pure-C# reconciliation of approval-GATE node status from the approvals feed.
/// Approval gates are NOT jobs — they have no <c>JobExecution</c> row — so their DAG-node status cannot
/// come from the execution list. Instead it is derived from the live <see cref="PendingApproval"/> set:
/// a gate with a pending approval is <see cref="JobStatus.AwaitingApproval"/> ("waiting"); once the gate
/// is no longer pending it resolves <b>once</b> to <see cref="JobStatus.Completed"/> (approved /
/// auto-approved) or <see cref="JobStatus.Failed"/> (rejected / the batch ended Failed/Cancelled).
/// </summary>
/// <remarks>
/// There is no "approval resolved" hub event, so resolution is detected as a set delta: a StepId that was
/// in the previous <c>awaiting</c> set but is absent from the latest <c>pending</c> set has resolved. The
/// terminal write is one-shot (guarded by <c>resolved</c>) so a later step's failure can never re-colour
/// an already-approved gate, and a re-appearing pending entry cannot drag a resolved gate back to waiting.
/// </remarks>
public static class ApprovalGateReconciler
{
    /// <summary>
    /// Folds the latest <paramref name="pending"/> gate set into <paramref name="status"/>, advancing any
    /// gate that left <paramref name="previousAwaiting"/> to a terminal status (recorded in
    /// <paramref name="resolved"/>). Mutates <paramref name="resolved"/> and <paramref name="status"/>.
    /// </summary>
    /// <param name="pending">Gate StepIds with a live PendingApproval for the current batch.</param>
    /// <param name="previousAwaiting">Gate StepIds that were pending at the previous reconciliation.</param>
    /// <param name="batchFailed"><c>true</c> when the batch ended Failed/Cancelled (gate → Failed).</param>
    /// <param name="resolved">In/out: gates already given a terminal status (one-shot guard).</param>
    /// <param name="status">In/out: gate StepId → <see cref="JobStatus"/> overlay for the DAG map.</param>
    public static void Apply(
        IReadOnlySet<string> pending,
        IReadOnlySet<string> previousAwaiting,
        bool batchFailed,
        HashSet<string> resolved,
        Dictionary<string, JobStatus> status)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(previousAwaiting);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(status);

        // Still / newly pending → waiting (unless already resolved terminal — never drag back).
        foreach (var sid in pending)
            if (!resolved.Contains(sid))
                status[sid] = JobStatus.AwaitingApproval;

        // Was pending, now gone → resolve ONCE (resolved.Add returns false if already terminal).
        foreach (var sid in previousAwaiting)
            if (!pending.Contains(sid) && resolved.Add(sid))
                status[sid] = batchFailed ? JobStatus.Failed : JobStatus.Completed;
    }
}
