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
/// BYTE-FOR-BYTE preserved from the in-process executor.</para>
/// </remarks>
internal static class ParallelGroupRunner
{
    /// <summary>Executes a parallel-group step and joins per <see cref="ParallelGroupData.JoinPolicy"/>.</summary>
    /// <remarks>
    /// <paramref name="resumeShadowProbe"/> is the OPTIONAL cross-service resume idempotency probe,
    /// threaded into the shared <see cref="CrossServiceStepInvoker"/> so a cross-service CHILD that already
    /// terminated before a crash is not re-dispatched on resume. <c>null</c> on the trigger path keeps the
    /// child dispatch byte-for-byte (the trailing-optional accommodation: an optional parameter cannot
    /// precede the existing non-defaulted <paramref name="cancellationToken"/>).
    /// </remarks>
    public static async Task RunAsync(
        BatchDefinition def,
        string batchId,
        BatchStep groupStep,
        JobParameters initial,
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
                    // === LOCAL CHILD PATH (preserve byte-for-byte) ===
                    // A null, empty, or whitespace TargetService means "run here", consistent with the
                    // sequential executor and the trigger-time pre-flight.
                    var childExecId = IdGenerator.NewExecutionId();
                    var childWait = awaiter.WaitForTerminalAsync(childExecId, groupCts.Token);
                    try
                    {
                        await runner.TriggerInternalAsync(
                            child.Job.JobName,
                            MergeParameters(initial, child.Job.Parameters),
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
                    return terminal.Status;
                }
                else
                {
                    // === CROSS-SERVICE CHILD PATH ===
                    // Parallel semantics: a timeout/exception ends the shadow row Failed and returns Failed
                    // so the join policy decides; cancellation rethrows; the terminal status is returned raw
                    // (a Cancelled child stays observable to the join).
                    return await crossServiceInvoker.InvokeAsync(
                        def, batchId, child, initial, triggeredBy, throwOnFailure: false, groupCts.Token).ConfigureAwait(false);
                }
            }, groupCts.Token))
            .ToArray();

        switch (group.JoinPolicy)
        {
            case ParallelJoinPolicy.WaitAll:
                await Task.WhenAll(childTasks).ConfigureAwait(false);
                if (childTasks.Any(t => t.Result is JobStatus.Failed or JobStatus.Cancelled))
                {
                    throw new BatchStepFailureException("Parallel WaitAll group: one or more children failed/cancelled");
                }
                break;

            case ParallelJoinPolicy.WaitAny:
            {
                var remaining = childTasks.ToList();
                while (remaining.Count > 0)
                {
                    var winner = await Task.WhenAny(remaining).ConfigureAwait(false);
                    remaining.Remove(winner);
                    var status = await winner.ConfigureAwait(false);
                    if (status == JobStatus.Completed)
                    {
                        groupCts.Cancel();
                        return;
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
                var pending = childTasks.ToList();
                while (pending.Count > 0)
                {
                    var winner = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(winner);
                    var status = await winner.ConfigureAwait(false);
                    if (status == JobStatus.Completed)
                    {
                        successes++;
                    }
                    else
                    {
                        failures++;
                    }
                    if (successes >= quorum)
                    {
                        groupCts.Cancel();
                        return;
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
    /// Merges initial batch parameters with the static per-step parameters; step keys win.
    /// Defensive copy on the merge result (this is NOT a trusted callsite).
    /// </summary>
    internal static JobParameters MergeParameters(JobParameters initial, IReadOnlyDictionary<string, object?>? stepParameters)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (stepParameters is null || stepParameters.Count == 0)
        {
            return initial;
        }
        var merged = new Dictionary<string, object?>(initial.Values, StringComparer.Ordinal);
        foreach (var (k, v) in stepParameters)
        {
            merged[k] = v;
        }
        return new JobParameters(merged);
    }
}
