using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Transport;
using UKBatch.Internal;

namespace UKBatch.Runtime;

/// <summary>
/// Fan-out / fan-in executor for <see cref="BatchStepType.ParallelGroup"/> steps.
/// Uses the internal <see cref="IJobRunnerInternal"/> seam and the
/// <see cref="IJobExecutionAwaiter"/> seam.
/// </summary>
/// <remarks>
/// <para>Each child step follows the 4-step awaiter-before-trigger ordering:
/// pre-allocate execution id; register waiter; trigger with the predefined id; await terminal.</para>
/// <para>Cancellation: a shared linked CTS lets WaitAny / WaitMajority cancel their siblings.</para>
/// <para>The signature threads <see cref="ITransport"/> + <c>thisServiceName</c> +
/// <see cref="TimeProvider"/> through the cross-service child path. The local-child path is
/// BYTE-FOR-BYTE preserved from the in-process executor apart from output forwarding.</para>
/// <para>Output forwarding: children all receive the SAME accumulated-output snapshot captured at group
/// entry (they do NOT observe one another's output — concurrent children have no defined order). After
/// the join, the outputs of the join-satisfying children are folded together in <see cref="BatchStep.Order"/>
/// ascending (last writer wins) and returned, so a later sequential step sees them deterministically.</para>
/// </remarks>
internal static class ParallelGroupRunner
{
    /// <summary>The terminal status and produced outputs of one parallel child.</summary>
    private readonly record struct ChildResult(int Order, JobStatus Status, IReadOnlyDictionary<string, object?>? Outputs);

    /// <summary>
    /// Executes a parallel-group step and joins per <see cref="ParallelGroupData.JoinPolicy"/>. Returns the
    /// merged outputs of the join-satisfying children (empty when none produced output), for the caller to
    /// fold into the run's accumulated outputs.
    /// </summary>
    /// <remarks>
    /// <paramref name="accumulatedOutputs"/> is the snapshot of prior steps' outputs, merged into every
    /// child's parameters (under the batch-initial set, beneath the child's own static parameters).
    /// <paramref name="resumeShadowProbe"/> is the OPTIONAL cross-service resume idempotency probe,
    /// threaded into the shared <see cref="CrossServiceStepInvoker"/> so a cross-service CHILD that already
    /// terminated before a crash is not re-dispatched on resume. <c>null</c> on the trigger path keeps the
    /// child dispatch byte-for-byte (the trailing-optional accommodation: an optional parameter cannot
    /// precede the existing non-defaulted <paramref name="cancellationToken"/>).
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, object?>> RunAsync(
        BatchDefinition def,
        string batchId,
        BatchStep groupStep,
        JobParameters initial,
        IReadOnlyDictionary<string, object?>? accumulatedOutputs,
        string? triggeredBy,
        IJobRunnerInternal runner,
        IJobExecutionAwaiter awaiter,
        ITransport transport,
        string? thisServiceName,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        IResumeShadowProbe? resumeShadowProbe = null)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentNullException.ThrowIfNull(groupStep);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(awaiter);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var group = groupStep.ParallelGroup
            ?? throw new InvalidOperationException($"Step {groupStep.StepId} is ParallelGroup but has no payload.");

        var crossServiceInvoker = CrossServiceStepInvoker.Create(transport, runner, thisServiceName, timeProvider, resumeShadowProbe);

        using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var children = group.Steps.OrderBy(s => s.Order).ToList();

        var childTasks = children
            .Select(child => Task.Run(async () =>
            {
                if (child.Job is null)
                {
                    throw new InvalidOperationException($"ParallelGroup child step {child.StepId} is not a Job step (nested groups are forbidden in v0.1).");
                }

                if (string.IsNullOrWhiteSpace(child.Job.TargetService))
                {
                    // === LOCAL CHILD PATH ===
                    // A null, empty, or whitespace TargetService means "run here", consistent with the
                    // sequential executor and the trigger-time pre-flight.
                    var childExecId = IdGenerator.NewExecutionId();
                    var childWait = awaiter.WaitForTerminalAsync(childExecId, groupCts.Token);
                    try
                    {
                        await runner.TriggerInternalAsync(
                            child.Job.JobName,
                            MergeParameters(initial, accumulatedOutputs, child.Job.Parameters),
                            triggeredBy,
                            batchId,
                            child.StepId,
                            predefinedExecutionId: childExecId,
                            batchDefinitionId: def.Id,
                            groupCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Trigger threw before the dispatcher accepted the request (e.g. unregistered
                        // job name). The watch loop will never complete the TCS — release it here, then
                        // rethrow so Task.WhenAll / WhenAny observes the failure.
                        awaiter.CancelWaiter(childExecId);
                        throw;
                    }
                    var terminal = await childWait.ConfigureAwait(false);
                    return new ChildResult(child.Order, terminal.Status, terminal.Outputs);
                }
                else
                {
                    // === CROSS-SERVICE CHILD PATH ===
                    // Parallel semantics: a timeout/exception ends the shadow row Failed and returns Failed
                    // so the join policy decides; cancellation rethrows; the terminal status is returned raw
                    // (a Cancelled child stays observable to the join). A Completed child's produced outputs
                    // ride the result and fold in join order; a non-Completed child carries null (never folds).
                    var result = await crossServiceInvoker.InvokeAsync(
                        def, batchId, child, initial, accumulatedOutputs, triggeredBy, throwOnFailure: false, groupCts.Token).ConfigureAwait(false);
                    return new ChildResult(child.Order, result.Status, result.Outputs);
                }
            }, groupCts.Token))
            .ToArray();

        switch (group.JoinPolicy)
        {
            case ParallelJoinPolicy.WaitAll:
            {
                await Task.WhenAll(childTasks).ConfigureAwait(false);
                var results = childTasks.Select(t => t.Result).ToList();
                if (results.Any(r => r.Status is JobStatus.Failed or JobStatus.Cancelled))
                {
                    throw new BatchStepFailureException("Parallel WaitAll group: one or more children failed/cancelled");
                }
                return FoldByOrder(results);
            }

            case ParallelJoinPolicy.WaitAny:
            {
                var remaining = childTasks.ToList();
                while (remaining.Count > 0)
                {
                    var winner = await Task.WhenAny(remaining).ConfigureAwait(false);
                    remaining.Remove(winner);
                    var result = await winner.ConfigureAwait(false);
                    if (result.Status == JobStatus.Completed)
                    {
                        groupCts.Cancel();
                        return FoldByOrder([result]);
                    }
                }
                throw new BatchStepFailureException("Parallel WaitAny group: all children failed");
            }

            case ParallelJoinPolicy.WaitMajority:
            {
                var n = childTasks.Length;
                var quorum = (n / 2) + 1;
                var successes = 0;
                var failures = 0;
                var winners = new List<ChildResult>();
                var pending = childTasks.ToList();
                while (pending.Count > 0)
                {
                    var winner = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(winner);
                    var result = await winner.ConfigureAwait(false);
                    if (result.Status == JobStatus.Completed)
                    {
                        successes++;
                        winners.Add(result);
                    }
                    else
                    {
                        failures++;
                    }
                    if (successes >= quorum)
                    {
                        groupCts.Cancel();
                        return FoldByOrder(winners);
                    }
                    if (failures > n - quorum)
                    {
                        groupCts.Cancel();
                        throw new BatchStepFailureException(
                            $"Parallel WaitMajority group: quorum unreachable ({successes}/{n} success, {failures} fail).");
                    }
                }
                throw new BatchStepFailureException("Parallel WaitMajority group: did not reach quorum");
            }

            default:
                throw new InvalidOperationException($"Unknown ParallelJoinPolicy: {group.JoinPolicy}");
        }
    }

    /// <summary>
    /// Folds the outputs of the given children into one dictionary, applied in <see cref="ChildResult.Order"/>
    /// ascending so the highest-Order child wins a key collision — a deterministic last-writer-wins for steps
    /// that run after the group.
    /// </summary>
    private static Dictionary<string, object?> FoldByOrder(IEnumerable<ChildResult> results)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var result in results.OrderBy(r => r.Order))
        {
            if (result.Outputs is { Count: > 0 } outputs)
            {
                foreach (var (k, v) in outputs)
                {
                    merged[k] = v;
                }
            }
        }
        return merged;
    }

    /// <summary>
    /// Merges, in precedence order, the initial batch parameters, the accumulated prior-step outputs, and
    /// the static per-step parameters; later sources win (so a step's own static parameter beats a
    /// forwarded output, which beats a batch-initial value). Returns the SAME <paramref name="initial"/>
    /// reference when both extra sources are empty (the no-forwarding fast path). Defensive copy on the
    /// merge result (this is NOT a trusted callsite).
    /// </summary>
    internal static JobParameters MergeParameters(
        JobParameters initial,
        IReadOnlyDictionary<string, object?>? accumulatedOutputs,
        IReadOnlyDictionary<string, object?>? stepParameters)
    {
        ArgumentNullException.ThrowIfNull(initial);
        var hasAccumulated = accumulatedOutputs is { Count: > 0 };
        var hasStep = stepParameters is { Count: > 0 };
        if (!hasAccumulated && !hasStep)
        {
            return initial;
        }
        var merged = new Dictionary<string, object?>(initial.Values, StringComparer.Ordinal);
        if (hasAccumulated)
        {
            foreach (var (k, v) in accumulatedOutputs!)
            {
                merged[k] = v;
            }
        }
        if (hasStep)
        {
            foreach (var (k, v) in stepParameters!)
            {
                merged[k] = v;
            }
        }
        return new JobParameters(merged);
    }
}
