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
        CancellationToken cancellationToken)
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

        using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var children = group.Steps.OrderBy(s => s.Order).ToList();

        var childTasks = children
            .Select(child => Task.Run(async () =>
            {
                if (child.Job is null)
                {
                    throw new InvalidOperationException($"ParallelGroup child step {child.StepId} is not a Job step (nested groups are forbidden in v0.1).");
                }

                if (child.Job.TargetService is null)
                {
                    // === LOCAL CHILD PATH (preserve byte-for-byte) ===
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
                    // === CROSS-SERVICE CHILD PATH (mirrors BatchExecutor.RunCrossServiceStepAsync) ===
                    if (string.IsNullOrWhiteSpace(thisServiceName))
                    {
                        throw new InvalidOperationException(
                            $"ParallelGroup child step {child.StepId} (cross-service to '{child.Job.TargetService}') " +
                            "requires the host's service identity. See UKBatchOptions.ThisServiceName / UKBATCH_SERVICE_NAME.");
                    }
                    var msg = new JobMessage
                    {
                        MessageId = IdGenerator.NewMessageId(),
                        CorrelationId = null,
                        JobName = child.Job.JobName,
                        SourceService = thisServiceName,
                        TargetService = child.Job.TargetService,
                        BatchId = batchId,
                        BatchStepId = child.StepId,
                        Parameters = MergeParameters(initial, child.Job.Parameters).Values,
                        Headers = new Dictionary<string, string>(StringComparer.Ordinal),
                        EnqueuedAtUtc = timeProvider.GetUtcNow(),
                        AttemptNumber = 1,
                    };
                    var timeout = child.Job.TimeoutSeconds is int t && t > 0
                        ? TimeSpan.FromSeconds(t)
                        : TimeSpan.FromMinutes(5);

                    // === Cross-service execution tracking (mirror of BatchExecutor) ===
                    // Mint a server-side SHADOW row in Running so the dashboard reflects remote-worker work.
                    var childNow = timeProvider.GetUtcNow();
                    var childExecId = IdGenerator.NewExecutionId();
                    var childRunning = new JobExecution
                    {
                        ExecutionId = childExecId,
                        JobName = child.Job.JobName,
                        BatchId = batchId,
                        BatchStepId = child.StepId,
                        BatchDefinitionId = def.Id,
                        Status = JobStatus.Running,
                        Parameters = msg.Parameters,
                        EnqueuedAtUtc = childNow,
                        StartedAtUtc = childNow,
                        CompletedAtUtc = null,
                        AttemptNumber = 1,
                        MaxRetries = 0,
                        LastError = null,
                        Processed = 0,
                        Failed = 0,
                        Total = null,
                        TriggeredBy = triggeredBy,
                        WorkerName = child.Job.TargetService,
                    };
                    await runner.RecordCrossServiceStartAsync(childRunning, groupCts.Token).ConfigureAwait(false);

                    JobResult result;
                    try
                    {
                        result = await transport.RequestReplyAsync(
                            child.Job.TargetService!, msg, timeout, groupCts.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException tex)
                    {
                        await runner.RecordCrossServiceEndAsync(
                            childExecId, FailedResult(childExecId, $"timed out after {timeout}: {tex.Message}", childNow), CancellationToken.None).ConfigureAwait(false);
                        return JobStatus.Failed;   // Parallel join treats this as a child failure.
                    }
                    catch (OperationCanceledException) when (groupCts.IsCancellationRequested)
                    {
                        // Running -> Cancelled is ILLEGAL — write Failed (CT-decoupled) so the row lands
                        // terminal as siblings cancel, THEN rethrow (group-level cancellation must bubble up).
                        await runner.RecordCrossServiceEndAsync(
                            childExecId, FailedResult(childExecId, "cross-service step cancelled (host shutdown / batch cancel)", childNow), CancellationToken.None).ConfigureAwait(false);
                        throw;   // Group-level cancellation; bubble up.
                    }
                    catch (Exception ex)
                    {
                        await runner.RecordCrossServiceEndAsync(
                            childExecId, FailedResult(childExecId, ex.Message, childNow), CancellationToken.None).ConfigureAwait(false);
                        return JobStatus.Failed;
                    }

                    // Persist the worker's terminal status (the join policy decides what failures mean).
                    await runner.RecordCrossServiceEndAsync(childExecId, result, CancellationToken.None).ConfigureAwait(false);
                    return result.Status;
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

    /// <summary>
    /// Builds a terminal <see cref="JobResult"/> in <see cref="JobStatus.Failed"/> for the
    /// cross-service child shadow-row end-update (transport throw / timeout / cancel arms). Mirrors the
    /// <c>BatchExecutor.FailedResult</c> helper (a small duplication).
    /// </summary>
    private static JobResult FailedResult(string execId, string error, DateTimeOffset completedAt)
        => new() { ExecutionId = execId, Status = JobStatus.Failed, ErrorMessage = error, CompletedAtUtc = completedAt };
}
