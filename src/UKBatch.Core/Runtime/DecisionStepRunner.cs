using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Transport;
using UKBatch.Internal;

namespace UKBatch.Runtime;

/// <summary>
/// Routing executor for <see cref="BatchStepType.Decision"/> steps: evaluates the branches in order, runs the
/// first whose condition holds (or the else/default branch), and records every other branch skipped. Uses the
/// same internal <see cref="IJobRunnerInternal"/> + <see cref="IJobExecutionAwaiter"/> seams as the sequential
/// executor, so a local winner follows the 4-step awaiter-before-trigger ordering and a cross-service winner
/// runs through the shared <see cref="CrossServiceStepInvoker"/>. The winner's outputs fold into the run's
/// accumulated outputs; the losing branches never run (recorded <see cref="JobStatus.Skipped"/>, never
/// compensated).
/// </summary>
/// <remarks>
/// A decision is ONE execution unit to the batch loop (like a parallel group): the cursor advances past it
/// only after it completes, and a compensator attached to the decision step compensates it as a whole. The
/// winner runs under its own branch <see cref="DecisionBranch.StepId"/>, so its execution row, cross-service
/// shadow, and resume dedupe all correlate with no extra plumbing.
/// </remarks>
internal static class DecisionStepRunner
{
    /// <summary>Non-null empty result returned when the winner (or the pass-through) produces no outputs.</summary>
    private static readonly IReadOnlyDictionary<string, object?> EmptyOutputs =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Empty skip set used on the trigger path, where no prior Skipped rows exist to dedupe against.</summary>
    private static readonly IReadOnlySet<string> EmptySkipSet = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Evaluates the decision, runs the winning branch, records the losers skipped, and returns the winner's
    /// produced outputs (empty when there is no winner or the winner produced none).
    /// </summary>
    /// <remarks>
    /// <paramref name="accumulatedOutputs"/> is the snapshot of prior steps' outputs, used to route (merged
    /// under the batch-initial parameters) and merged into the winner's parameters at dispatch beneath its own
    /// static parameters. <paramref name="resumeShadowProbe"/> is the OPTIONAL resume idempotency probe:
    /// <c>null</c> on the trigger path keeps routing and dispatch byte-for-byte; bound on the resume path it
    /// (a) skips re-recording losers already recorded before a crash and (b) short-circuits a local winner that
    /// already completed. A cross-service winner inherits its own skip through the shared invoker.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, object?>> RunAsync(
        BatchDefinition def,
        string batchId,
        BatchStep decisionStep,
        JobParameters initial,
        IReadOnlyDictionary<string, object?>? accumulatedOutputs,
        string? triggeredBy,
        IJobRunnerInternal runner,
        IJobExecutionAwaiter awaiter,
        ITransport transport,
        string? thisServiceName,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken,
        IResumeShadowProbe? resumeShadowProbe = null)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentNullException.ThrowIfNull(decisionStep);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(awaiter);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var data = decisionStep.Decision
            ?? throw new InvalidOperationException($"Step {decisionStep.StepId} is Decision but has no payload.");
        var branches = data.Branches;

        // Route against the same view a step would receive at dispatch: initial parameters plus forwarded
        // outputs. A branch's own static parameters merge only at dispatch (for the winner), below.
        var merged = ParallelGroupRunner.MergeParameters(initial, accumulatedOutputs, null);

        // Winner = the first branch that is an else (null condition) or whose condition holds. May be none.
        DecisionBranch? winner = null;
        foreach (var branch in branches)
        {
            if (branch.When is null || StepConditionEvaluator.Evaluate(branch.When, merged))
            {
                winner = branch;
                break;
            }
        }

        // Resume-only loser dedupe: read the already-skipped set once so a mid-decision resume does not write
        // a second Skipped row for a branch already skipped before the crash. On the trigger path the probe is
        // null, so this is the empty set and every loser is recorded fresh.
        var alreadySkipped = resumeShadowProbe is not null
            ? await resumeShadowProbe.GetSkippedStepIdsAsync(batchId, cancellationToken).ConfigureAwait(false)
            : EmptySkipSet;

        // Record every non-winner branch as Skipped (visible, grey, never compensated). When there is no
        // winner, every branch is a loser and all are recorded. Reference identity — not value equality —
        // distinguishes the winner so two value-equal branches do not both escape the loser recording.
        foreach (var branch in branches)
        {
            if (ReferenceEquals(branch, winner) || alreadySkipped.Contains(branch.StepId))
            {
                continue;
            }
            await runner.RecordSkippedStepAsync(
                batchId, SynthesizeBranchStep(decisionStep, branch), def.Id, triggeredBy, cancellationToken).ConfigureAwait(false);
        }

        if (winner is null)
        {
            // No branch matched and there is no else: the decision passes through, forwarding nothing. This is
            // usually an authoring gap (a decision none of whose branches can be reached), so log it loudly.
            logger.LogWarning(
                "Batch {Batch} decision step {Step} matched no branch and has no else; all branches skipped, proceeding to the next step.",
                batchId, decisionStep.StepId);
            return EmptyOutputs;
        }

        var winnerJob = winner.Job;
        if (string.IsNullOrWhiteSpace(winnerJob.TargetService))
        {
            // === LOCAL WINNER ===
            // Resume idempotency: a prior attempt may have already COMPLETED the winner. Reuse its outputs
            // instead of re-running (a completed financial step must not run twice). Null probe / no prior
            // completion → dispatch proceeds unchanged.
            if (resumeShadowProbe is not null)
            {
                var prior = await resumeShadowProbe
                    .TryGetCompletedStatusAsync(batchId, winner.StepId, cancellationToken).ConfigureAwait(false);
                if (prior is { } completion)
                {
                    return completion.Outputs ?? EmptyOutputs;
                }
            }

            // Same 4-step awaiter-before-trigger ordering as the sequential Job arm.
            var execId = IdGenerator.NewExecutionId();
            var waitTask = awaiter.WaitForTerminalAsync(execId, cancellationToken);
            try
            {
                await runner.TriggerInternalAsync(
                    winnerJob.JobName,
                    ParallelGroupRunner.MergeParameters(initial, accumulatedOutputs, winnerJob.Parameters),
                    triggeredBy,
                    batchId,
                    winner.StepId,
                    predefinedExecutionId: execId,
                    batchDefinitionId: def.Id,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Trigger threw before the dispatcher accepted the request (e.g. unregistered job name). The
                // watch loop will never complete the TCS — release it here, then rethrow so the executor's
                // per-step failure routing applies.
                awaiter.CancelWaiter(execId);
                throw;
            }
            var terminal = await waitTask.ConfigureAwait(false);
            if (terminal.Status is JobStatus.Failed or JobStatus.Cancelled)
            {
                throw new BatchStepFailureException($"Decision branch {winner.StepId} terminated as {terminal.Status}: {terminal.LastError}");
            }
            return terminal.Outputs ?? EmptyOutputs;
        }

        // === CROSS-SERVICE WINNER ===
        // The shared invoker owns the cross-service resume skip (a completed shadow row short-circuits its
        // dispatch), so do NOT pre-probe here — mirror the parallel-group cross-service child exactly.
        var crossServiceInvoker = CrossServiceStepInvoker.Create(transport, runner, thisServiceName, timeProvider, resumeShadowProbe);
        var result = await crossServiceInvoker.InvokeAsync(
            def, batchId, SynthesizeBranchStep(decisionStep, winner), initial, accumulatedOutputs, triggeredBy, throwOnFailure: true, cancellationToken).ConfigureAwait(false);
        return result.Outputs ?? EmptyOutputs;
    }

    /// <summary>
    /// Projects a branch to a runnable Job step keyed by the branch <see cref="DecisionBranch.StepId"/>: used
    /// to record a loser skipped (its <see cref="DecisionBranch.When"/> feeds the skip audit note) and to run a
    /// cross-service winner through the shared invoker. The synthesized step carries the decision's
    /// <see cref="BatchStep.Order"/> so the row sorts alongside its siblings.
    /// </summary>
    private static BatchStep SynthesizeBranchStep(BatchStep decisionStep, DecisionBranch branch) =>
        new()
        {
            StepId = branch.StepId,
            Order = decisionStep.Order,
            StepType = BatchStepType.Job,
            Job = branch.Job,
            Condition = branch.When,
            ParallelGroup = null,
            Approval = null,
            Compensation = null,
            Metadata = null,
        };
}
