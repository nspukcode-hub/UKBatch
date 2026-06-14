using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Api.Approvals;

namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// Pure-C# reconciliation of approval-GATE node status from the batch's gate feed.
/// Approval gates are NOT jobs — they have no <c>JobExecution</c> row — so their DAG-node status cannot
/// come from the execution list. Instead each gate carries its OWN recorded decision: a gate that is still
/// pending is <see cref="JobStatus.AwaitingApproval"/> ("waiting"); a decided gate is
/// <see cref="JobStatus.Completed"/> when approved or auto-approved, and <see cref="JobStatus.Failed"/>
/// for any other terminal outcome (rejected, dismissed, timed-out-to-fail, cancelled, interrupted).
/// </summary>
/// <remarks>
/// Each gate carries its own immutable outcome, so the overlay is a direct map with no cross-gate
/// inference: an earlier approved gate stays green even if a later gate or step fails, and re-applying
/// the same feed yields the same result (decisions are immutable in the store, so the read is idempotent).
/// </remarks>
public static class ApprovalGateReconciler
{
    /// <summary>
    /// Overlays each gate's own decided outcome onto <paramref name="status"/> (gate StepId →
    /// <see cref="JobStatus"/>). Pending → waiting; approved/auto-approved → completed; any other
    /// terminal outcome → failed. Mutates <paramref name="status"/>; the last writer wins on the rare
    /// duplicate StepId (two gates on one step is a malformed definition).
    /// </summary>
    /// <param name="gates">Every gate (pending AND decided) for the current batch run.</param>
    /// <param name="status">In/out: gate StepId → <see cref="JobStatus"/> overlay for the DAG map.</param>
    public static void Apply(IReadOnlyList<ApprovalGateViewDto> gates, Dictionary<string, JobStatus> status)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(status);

        foreach (var g in gates)
        {
            status[g.BatchStepId] = g.Status switch
            {
                ApprovalRecordStatus.Pending => JobStatus.AwaitingApproval,
                _ => g.Outcome switch
                {
                    ApprovalRecordOutcome.Approved or ApprovalRecordOutcome.AutoApproved => JobStatus.Completed,
                    _ => JobStatus.Failed,   // Rejected / Dismissed / TimedOutFail / Cancelled / Interrupted (or a decided record with no outcome — fail safe)
                },
            };
        }
    }
}
