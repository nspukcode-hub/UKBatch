using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;

namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// Pure-C# reconciliation of a decision DIAMOND node's status from its branches. The diamond
/// (the decision step's <see cref="BatchStep.StepId"/>) has no <c>JobExecution</c> row — routing itself
/// produces no execution — so its colour is derived from the branch rows: the winner runs under its branch
/// id (green when done), the losers are recorded <see cref="JobStatus.Skipped"/>.
/// </summary>
/// <remarks>
/// Priority is fail-first, then in-flight, then decided, then routed-nowhere: a failed winner paints the
/// diamond failed; an in-flight winner (pending/running) paints it running; a completed winner paints it
/// completed; and a decision whose branches are all skipped (no match, no else) paints it skipped. Before any
/// branch has a row the diamond has no entry and renders "not started" like any other node. Re-applying the
/// same map is idempotent.
/// </remarks>
public static class DecisionStatusReconciler
{
    /// <summary>
    /// Overlays each decision diamond's derived status onto <paramref name="status"/> (decision StepId →
    /// <see cref="JobStatus"/>), reading the branch statuses already present in the map. Mutates
    /// <paramref name="status"/>. A decision with no branch rows yet is left untouched (not-started).
    /// </summary>
    /// <param name="steps">The batch's top-level steps (Decision steps carry their branches).</param>
    /// <param name="status">In/out: StepId → <see cref="JobStatus"/> overlay for the DAG map.</param>
    public static void Apply(IReadOnlyList<BatchStep> steps, Dictionary<string, JobStatus> status)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(status);

        foreach (var step in steps)
        {
            if (step is not { StepType: BatchStepType.Decision, Decision: { } decision })
            {
                continue;
            }

            var derived = DeriveDiamondStatus(decision, status);
            if (derived is { } s)
            {
                status[step.StepId] = s;
            }
        }
    }

    private static JobStatus? DeriveDiamondStatus(DecisionStepData decision, Dictionary<string, JobStatus> status)
    {
        var anyPresent = false;
        var anyFailed = false;
        var anyCancelled = false;
        var anyInFlight = false;
        var anyCompleted = false;

        foreach (var branch in decision.Branches)
        {
            if (!status.TryGetValue(branch.StepId, out var s))
            {
                continue;
            }
            anyPresent = true;
            switch (s)
            {
                case JobStatus.Failed:
                    anyFailed = true;
                    break;
                case JobStatus.Cancelled or JobStatus.Cancelling:
                    anyCancelled = true;
                    break;
                case JobStatus.Running or JobStatus.Retrying or JobStatus.AwaitingApproval
                    or JobStatus.Pending or JobStatus.Scheduled:
                    anyInFlight = true;
                    break;
                case JobStatus.Completed:
                    anyCompleted = true;
                    break;
                // Skipped (and any other terminal) leaves the diamond to fall through to "routed nowhere".
            }
        }

        if (!anyPresent) return null;                 // no branch has run — leave the diamond not-started
        if (anyFailed) return JobStatus.Failed;       // the chosen branch failed → the decision failed
        if (anyCancelled) return JobStatus.Cancelled;
        if (anyInFlight) return JobStatus.Running;     // the winner is executing
        if (anyCompleted) return JobStatus.Completed;  // the winner finished → decided
        return JobStatus.Skipped;                      // every branch skipped → no match, no else
    }
}
