using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;
using UKBatch.Internal;

namespace UKBatch.Runtime;

/// <summary>
/// Single dispatch path for a cross-service <see cref="BatchStepType.Job"/> step, shared by the
/// sequential executor and the parallel-group fan-out so the two cannot drift. Fails fast when the
/// host has no service identity, mints a server-side shadow execution row in
/// <see cref="JobStatus.Running"/>, performs the transport request/reply, then ends that shadow row
/// at the worker's terminal status. The shadow row exists so the dashboard (run-detail, completion
/// counts, DAG coloring, run history) reflects work that actually runs on a remote worker.
/// </summary>
internal sealed class CrossServiceStepInvoker
{
    private readonly ITransport _transport;
    private readonly IJobRunnerInternal _runner;
    private readonly string? _thisServiceName;
    private readonly TimeProvider _timeProvider;
    private readonly IResumeShadowProbe? _resumeShadowProbe;

    public CrossServiceStepInvoker(
        ITransport transport,
        IJobRunnerInternal runner,
        string? thisServiceName,
        TimeProvider timeProvider,
        IResumeShadowProbe? resumeShadowProbe = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _transport = transport;
        _runner = runner;
        _thisServiceName = thisServiceName;
        _timeProvider = timeProvider;
        _resumeShadowProbe = resumeShadowProbe;   // null on the trigger path: first-pass dispatch is unchanged
    }

    /// <summary>
    /// Single construction point shared by the sequential executor and the parallel-group fan-out so the
    /// resume shadow probe is threaded identically at BOTH sites. The probe is <c>null</c> on the trigger
    /// path (first-pass dispatch unchanged) and bound on the resume path so a completed cross-service step
    /// is not re-dispatched. Centralizing the build avoids the two-site drift that threading by hand invites.
    /// </summary>
    public static CrossServiceStepInvoker Create(
        ITransport transport,
        IJobRunnerInternal runner,
        string? thisServiceName,
        TimeProvider timeProvider,
        IResumeShadowProbe? resumeShadowProbe)
        => new(transport, runner, thisServiceName, timeProvider, resumeShadowProbe);

    /// <summary>
    /// Dispatches one cross-service Job step and returns its terminal <see cref="JobStatus"/>.
    /// </summary>
    /// <param name="def">The owning batch definition (its id tags the shadow row).</param>
    /// <param name="batchId">Run id of the executing batch.</param>
    /// <param name="step">The Job step; its <see cref="JobStepData.TargetService"/> MUST be set.</param>
    /// <param name="initial">Initial batch parameters, merged with the step's static parameters.</param>
    /// <param name="triggeredBy">Identity recorded on the shadow row, if any.</param>
    /// <param name="throwOnFailure">
    /// When <c>true</c> (sequential caller), a Failed/Cancelled terminal status as well as a transport
    /// timeout or exception throw <see cref="BatchStepFailureException"/>. When <c>false</c>
    /// (parallel-group child), a timeout or exception ends the shadow row Failed and RETURNS
    /// <see cref="JobStatus.Failed"/> so the join policy decides, and the terminal status is returned
    /// raw — including a possible <see cref="JobStatus.Cancelled"/> — because the join policy must be
    /// able to observe a cancelled child. The worker-Cancelled→Failed normalization is owned by the
    /// runner's end-record path, so it is deliberately not duplicated here.
    /// </param>
    /// <param name="transportCancellationToken">
    /// Cancellation for the start-record and the transport call. When it is cancelled the shadow row
    /// is always ended Failed and the <see cref="OperationCanceledException"/> rethrows regardless of
    /// <paramref name="throwOnFailure"/> — cancellation must bubble.
    /// </param>
    public async Task<JobStatus> InvokeAsync(
        BatchDefinition def,
        string batchId,
        BatchStep step,
        JobParameters initial,
        string? triggeredBy,
        bool throwOnFailure,
        CancellationToken transportCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(initial);

        var job = step.Job
            ?? throw new InvalidOperationException($"Step {step.StepId} is Job but has no payload.");

        // Fail-fast: SourceService is `required string`; without resolved identity the receiver
        // would JsonException at deserialize. Throw here with an operator-actionable message.
        if (string.IsNullOrWhiteSpace(_thisServiceName))
        {
            throw new InvalidOperationException(
                $"Step {step.StepId} (cross-service to '{job.TargetService}') requires the host's " +
                "service identity to be set. Configure UKBatchOptions.ThisServiceName " +
                "(appsettings 'UKBatch:ThisServiceName' or builder.Configure(o => o.ThisServiceName = ...)) " +
                "OR set the UKBATCH_SERVICE_NAME environment variable.");
        }

        // Resume idempotency: a prior attempt may have already COMPLETED this cross-service step. If a
        // Completed shadow row exists for (run, step), skip the transport call and return Completed. The
        // probe returns ONLY Completed (a non-terminal/orphan/remote-failed row does not prove the remote
        // work finished), so an in-flight-at-crash or reaper-tombstoned step is re-dispatched (the
        // documented at-least-once replay) — the symmetric counterpart of an Interrupted gate re-opening.
        // On the first pass (and whenever the probe is unbound) there is no prior Completed row, so dispatch
        // proceeds unchanged.
        if (_resumeShadowProbe is not null)
        {
            var priorCompleted = await _resumeShadowProbe
                .TryGetCompletedStatusAsync(batchId, step.StepId, transportCancellationToken).ConfigureAwait(false);
            if (priorCompleted is { } prior)
            {
                return prior;   // always JobStatus.Completed; skip re-dispatch
            }
        }

        var msg = new JobMessage
        {
            MessageId = IdGenerator.NewMessageId(),
            CorrelationId = null,
            JobName = job.JobName,
            SourceService = _thisServiceName,
            TargetService = job.TargetService,
            BatchId = batchId,
            BatchStepId = step.StepId,
            Parameters = ParallelGroupRunner.MergeParameters(initial, job.Parameters).Values,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            EnqueuedAtUtc = _timeProvider.GetUtcNow(),
            AttemptNumber = 1,
        };
        var timeout = job.TimeoutSeconds is int t && t > 0
            ? TimeSpan.FromSeconds(t)
            : TimeSpan.FromMinutes(5);

        // Mint a server-side SHADOW row in Running so the dashboard reflects remote-worker work.
        var now = _timeProvider.GetUtcNow();
        var execId = IdGenerator.NewExecutionId();
        var running = new JobExecution
        {
            ExecutionId = execId,
            JobName = job.JobName,
            BatchId = batchId,
            BatchStepId = step.StepId,
            BatchDefinitionId = def.Id,
            Status = JobStatus.Running,
            Parameters = msg.Parameters,
            EnqueuedAtUtc = now,
            StartedAtUtc = now,
            CompletedAtUtc = null,
            AttemptNumber = 1,
            MaxRetries = 0,   // shadow of a single remote dispatch; the orchestrator owns no cross-service retry.
            LastError = null,
            Processed = 0,
            Failed = 0,
            Total = null,
            TriggeredBy = triggeredBy,
            WorkerName = job.TargetService,
        };
        await _runner.RecordCrossServiceStartAsync(running, transportCancellationToken).ConfigureAwait(false);

        JobResult result;
        try
        {
            result = await _transport
                .RequestReplyAsync(job.TargetService!, msg, timeout, transportCancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException tex)
        {
            // Terminal audit write must land even if the caller token trips between this throw and the
            // record, so it is CT-decoupled (CancellationToken.None).
            await _runner.RecordCrossServiceEndAsync(
                execId, FailedResult(execId, $"timed out after {timeout}: {tex.Message}", now), CancellationToken.None).ConfigureAwait(false);
            if (throwOnFailure)
            {
                throw new BatchStepFailureException(
                    $"Step {step.StepId} (cross-service '{job.TargetService}') timed out after {timeout}: {tex.Message}");
            }
            return JobStatus.Failed;   // Parallel join treats this as a child failure.
        }
        catch (OperationCanceledException) when (transportCancellationToken.IsCancellationRequested)
        {
            // Running -> Cancelled is ILLEGAL (only Cancelling -> Cancelled exists). Write Failed,
            // CT-decoupled (CancellationToken.None) so the terminal row lands as the caller token trips,
            // THEN rethrow the OCE (cancellation must still propagate regardless of throwOnFailure).
            await _runner.RecordCrossServiceEndAsync(
                execId, FailedResult(execId, "cross-service step cancelled (host shutdown / batch cancel)", now), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Terminal audit write must land even if the caller token trips between this throw and the
            // record, so it is CT-decoupled (CancellationToken.None).
            await _runner.RecordCrossServiceEndAsync(
                execId, FailedResult(execId, ex.Message, now), CancellationToken.None).ConfigureAwait(false);
            if (throwOnFailure)
            {
                throw new BatchStepFailureException(
                    $"Step {step.StepId} (cross-service '{job.TargetService}') failed: {ex.Message}");
            }
            return JobStatus.Failed;
        }

        // Persist the worker's terminal status (Completed/Failed/Cancelled) BEFORE any throw — the row
        // MUST end terminal, not stuck Running. CT-decoupled so the audit write survives caller cancel.
        await _runner.RecordCrossServiceEndAsync(execId, result, CancellationToken.None).ConfigureAwait(false);

        if (throwOnFailure && result.Status is JobStatus.Failed or JobStatus.Cancelled)
        {
            throw new BatchStepFailureException(
                $"Step {step.StepId} (cross-service '{job.TargetService}') terminated as {result.Status}: {result.ErrorMessage}");
        }

        // Return the terminal status raw. The parallel join must be able to observe a Cancelled child;
        // the worker-Cancelled->Failed normalization lives in the runner's end-record path.
        return result.Status;
    }

    /// <summary>
    /// Builds a terminal <see cref="JobResult"/> in <see cref="JobStatus.Failed"/> for the
    /// cross-service shadow-row end-update (transport throw / timeout / cancel arms). The row MUST
    /// reach a terminal state and never orphan in <see cref="JobStatus.Running"/>.
    /// </summary>
    private static JobResult FailedResult(string execId, string error, DateTimeOffset completedAt)
        => new() { ExecutionId = execId, Status = JobStatus.Failed, ErrorMessage = error, CompletedAtUtc = completedAt };
}
