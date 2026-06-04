using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Transport;
using UKBatch.Internal;
using UKBatch.Validation;

namespace UKBatch.Runtime;

/// <summary>
/// Sequentially walks the DAG of <see cref="BatchStep"/>s in a <see cref="BatchDefinition"/>.
/// Per-step try/catch routes failures via <see cref="BatchDefinition.FailurePolicy"/>.
/// Uses the internal <see cref="IJobRunnerInternal"/> + <see cref="IApprovalGateCoordinator"/> +
/// <see cref="IJobExecutionAwaiter"/> seams.
/// </summary>
/// <remarks>
/// The ctor threads <see cref="ITransport"/> + <c>thisServiceName</c> + <see cref="TimeProvider"/>
/// through the cross-service branch in <see cref="RunStepAsync"/>. The local path is BYTE-FOR-BYTE
/// preserved from the in-process executor.
/// </remarks>
internal sealed class BatchExecutor
{
    private readonly IJobRunnerInternal _runner;
    private readonly IApprovalGateCoordinator _approvalCoordinator;
    private readonly IJobExecutionAwaiter _awaiter;
    private readonly ITransport _transport;
    private readonly string? _thisServiceName;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BatchExecutor> _logger;

    /// <summary>Constructs the executor.</summary>
    /// <param name="runner">Internal runner seam used for both local trigger dispatch and recursive batch composition.</param>
    /// <param name="approvalCoordinator">Coordinator awaited at <see cref="BatchStepType.ApprovalGate"/> steps.</param>
    /// <param name="awaiter">Terminal-state awaiter for the 4-step awaiter-before-trigger pattern.</param>
    /// <param name="transport">Pluggable transport adapter used by cross-service steps.</param>
    /// <param name="thisServiceName">
    /// Resolved service identity for outbound <see cref="JobMessage.SourceService"/>; <c>null</c>
    /// is permitted for receiver-only nodes. Cross-service steps fail-fast when this is null/whitespace.
    /// </param>
    /// <param name="timeProvider">Clock for <see cref="JobMessage.EnqueuedAtUtc"/>.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public BatchExecutor(
        IJobRunnerInternal runner,
        IApprovalGateCoordinator approvalCoordinator,
        IJobExecutionAwaiter awaiter,
        ITransport transport,
        string? thisServiceName,
        TimeProvider timeProvider,
        ILogger<BatchExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(approvalCoordinator);
        ArgumentNullException.ThrowIfNull(awaiter);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _approvalCoordinator = approvalCoordinator;
        _awaiter = awaiter;
        _transport = transport;
        _thisServiceName = thisServiceName;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs a batch definition end-to-end. Throws <see cref="OperationCanceledException"/> on
    /// cancellation; otherwise throws <see cref="InvalidOperationException"/> only when
    /// <see cref="BatchFailurePolicy.StopOnFailure"/> or <see cref="BatchFailurePolicy.Compensate"/>
    /// re-throws.
    /// </summary>
    public async Task RunAsync(
        BatchDefinition def,
        string batchId,
        JobParameters initial,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentNullException.ThrowIfNull(initial);

        var validation = BatchDefinitionValidator.Validate(def);
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors.Select(e => $"{e.PropertyPath}: {e.Message}"));
            throw new InvalidOperationException($"BatchDefinition {def.Id} validation failed: {errors}");
        }

        var orderedSteps = def.Steps.OrderBy(s => s.Order).ToList();
        Exception? firstFailure = null;

        foreach (var step in orderedSteps)
        {
            try
            {
                await RunStepAsync(def, batchId, step, initial, triggeredBy, cancellationToken).ConfigureAwait(false);
            }
            catch (BatchStepFailureException stepFailure)
            {
                switch (def.FailurePolicy)
                {
                    case BatchFailurePolicy.StopOnFailure:
                        throw;

                    case BatchFailurePolicy.ContinueOnFailure:
                        _logger.LogWarning(stepFailure, "Batch {Batch} step {Step} failed; continuing per ContinueOnFailure policy.", batchId, step.StepId);
                        firstFailure ??= stepFailure;
                        continue;

                    case BatchFailurePolicy.Compensate:
                        if (def.OnFailureSteps.Count == 0)
                        {
                            throw;
                        }
                        await RunCompensationAsync(def, batchId, initial, triggeredBy, cancellationToken).ConfigureAwait(false);
                        throw;

                    default:
                        throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        _ = firstFailure;
    }

    private async Task RunStepAsync(
        BatchDefinition def,
        string batchId,
        BatchStep step,
        JobParameters initial,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        switch (step.StepType)
        {
            case BatchStepType.Job:
            {
                if (step.Job is null)
                {
                    throw new InvalidOperationException($"Step {step.StepId} is Job but has no payload.");
                }

                if (step.Job.TargetService is null)
                {
                    // === LOCAL PATH (preserve byte-for-byte) ===
                    var execId = IdGenerator.NewExecutionId();
                    var waitTask = _awaiter.WaitForTerminalAsync(execId, cancellationToken);
                    try
                    {
                        await _runner.TriggerInternalAsync(
                            step.Job.JobName,
                            ParallelGroupRunner.MergeParameters(initial, step.Job.Parameters),
                            triggeredBy,
                            batchId,
                            step.StepId,
                            predefinedExecutionId: execId,
                            batchDefinitionId: def.Id,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Trigger threw before the dispatcher accepted the request (e.g. unregistered
                        // job name). The watch loop will never complete the TCS — release it here, then
                        // rethrow so the BatchExecutor's per-step failure routing applies.
                        _awaiter.CancelWaiter(execId);
                        throw;
                    }
                    var terminal = await waitTask.ConfigureAwait(false);
                    if (terminal.Status is JobStatus.Failed or JobStatus.Cancelled)
                    {
                        throw new BatchStepFailureException($"Step {step.StepId} terminated as {terminal.Status}: {terminal.LastError}");
                    }
                }
                else
                {
                    // === CROSS-SERVICE PATH ===
                    await RunCrossServiceStepAsync(def, batchId, step, initial, triggeredBy, cancellationToken).ConfigureAwait(false);
                }
                break;
            }

            case BatchStepType.ParallelGroup:
                await ParallelGroupRunner.RunAsync(
                    def, batchId, step, initial, triggeredBy,
                    _runner, _awaiter,
                    _transport, _thisServiceName, _timeProvider,
                    cancellationToken).ConfigureAwait(false);
                break;

            case BatchStepType.ApprovalGate:
            {
                if (step.Approval is null)
                {
                    throw new InvalidOperationException($"Step {step.StepId} is ApprovalGate but has no payload.");
                }
                await _approvalCoordinator.AwaitApprovalAsync(batchId, step.StepId, step.Approval, def.Name, def.Id, cancellationToken).ConfigureAwait(false);
                break;
            }

            default:
                _logger.LogWarning("Unknown BatchStepType {Type} on step {StepId}; treating as no-op (forward-compat).", step.StepType, step.StepId);
                break;
        }
    }

    /// <summary>
    /// Cross-service dispatch — extracted from <see cref="RunStepAsync"/> for readability.
    /// </summary>
    private async Task RunCrossServiceStepAsync(
        BatchDefinition def,
        string batchId,
        BatchStep step,
        JobParameters initial,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        // Fail-fast: SourceService is `required string`; without resolved identity the receiver
        // would JsonException at deserialize. Throw here with an operator-actionable message.
        if (string.IsNullOrWhiteSpace(_thisServiceName))
        {
            throw new InvalidOperationException(
                $"Step {step.StepId} (cross-service to '{step.Job!.TargetService}') requires the host's " +
                "service identity to be set. Configure UKBatchOptions.ThisServiceName " +
                "(appsettings 'UKBatch:ThisServiceName' or builder.Configure(o => o.ThisServiceName = ...)) " +
                "OR set the UKBATCH_SERVICE_NAME environment variable.");
        }

        var msg = new JobMessage
        {
            MessageId = IdGenerator.NewMessageId(),
            CorrelationId = null,
            JobName = step.Job!.JobName,
            SourceService = _thisServiceName,
            TargetService = step.Job.TargetService,
            BatchId = batchId,
            BatchStepId = step.StepId,
            Parameters = ParallelGroupRunner.MergeParameters(initial, step.Job.Parameters).Values,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            EnqueuedAtUtc = _timeProvider.GetUtcNow(),
            AttemptNumber = 1,
        };
        var timeout = step.Job.TimeoutSeconds is int t && t > 0
            ? TimeSpan.FromSeconds(t)
            : TimeSpan.FromMinutes(5);

        // === Cross-service execution tracking (insert before, update after) ===
        // Mint a server-side SHADOW row in Running so the dashboard (run-detail, completion counts,
        // DAG node coloring, run history) reflects work that actually runs on a remote worker.
        var now = _timeProvider.GetUtcNow();
        var execId = IdGenerator.NewExecutionId();
        var running = new JobExecution
        {
            ExecutionId = execId,
            JobName = step.Job.JobName,
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
            WorkerName = step.Job.TargetService,
        };
        await _runner.RecordCrossServiceStartAsync(running, cancellationToken).ConfigureAwait(false);

        JobResult result;
        try
        {
            result = await _transport
                .RequestReplyAsync(step.Job.TargetService!, msg, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException tex)
        {
            await _runner.RecordCrossServiceEndAsync(
                execId, FailedResult(execId, $"timed out after {timeout}: {tex.Message}", now), cancellationToken).ConfigureAwait(false);
            throw new BatchStepFailureException(
                $"Step {step.StepId} (cross-service '{step.Job.TargetService}') timed out after {timeout}: {tex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Running -> Cancelled is ILLEGAL (only Cancelling -> Cancelled exists). Write Failed,
            // CT-decoupled (CancellationToken.None) so the terminal row lands as the batch CT trips, THEN
            // rethrow the OCE (cancellation must still propagate to the batch loop).
            await _runner.RecordCrossServiceEndAsync(
                execId, FailedResult(execId, "cross-service step cancelled (host shutdown / batch cancel)", now), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await _runner.RecordCrossServiceEndAsync(
                execId, FailedResult(execId, ex.Message, now), cancellationToken).ConfigureAwait(false);
            throw new BatchStepFailureException(
                $"Step {step.StepId} (cross-service '{step.Job.TargetService}') failed: {ex.Message}");
        }

        // Persist the worker's terminal status (Completed/Failed/Cancelled) BEFORE any throw — the row
        // MUST end Failed, not stuck Running (the BatchStepFailureException case below).
        await _runner.RecordCrossServiceEndAsync(execId, result, cancellationToken).ConfigureAwait(false);

        if (result.Status is JobStatus.Failed or JobStatus.Cancelled)
        {
            throw new BatchStepFailureException(
                $"Step {step.StepId} (cross-service '{step.Job.TargetService}') terminated as {result.Status}: {result.ErrorMessage}");
        }
    }

    /// <summary>
    /// Builds a terminal <see cref="JobResult"/> in <see cref="JobStatus.Failed"/> for
    /// the cross-service shadow-row end-update. Used for transport throw / timeout / cancel arms — the
    /// row MUST reach a terminal state and never orphan in <see cref="JobStatus.Running"/>.
    /// </summary>
    private static JobResult FailedResult(string execId, string error, DateTimeOffset completedAt)
        => new() { ExecutionId = execId, Status = JobStatus.Failed, ErrorMessage = error, CompletedAtUtc = completedAt };

    private async Task RunCompensationAsync(
        BatchDefinition def,
        string batchId,
        JobParameters initial,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        foreach (var step in def.OnFailureSteps.OrderBy(s => s.Order))
        {
            try
            {
                await RunStepAsync(def, batchId, step, initial, triggeredBy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compensation step {Step} of batch {Batch} failed; continuing remaining compensation.", step.StepId, batchId);
                // do NOT rethrow — acyclic safety; we do not compensate the compensation.
            }
        }
    }
}
