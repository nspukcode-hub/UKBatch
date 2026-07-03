using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
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
    private readonly CrossServiceStepInvoker _crossServiceInvoker;
    private readonly IResumeShadowProbe? _resumeShadowProbe;
    private readonly IResumeGateProbe? _resumeGateProbe;
    private readonly Func<int, IReadOnlyDictionary<string, object?>, CancellationToken, Task>? _onStepCompleted;
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
    /// <param name="onStepCompleted">
    /// Optional resume seam, invoked after each step succeeds with the next-to-run step index AND the run's
    /// forwarded state (batch-initial parameters + accumulated outputs) to persist. <c>null</c> on paths
    /// that record no progress; bound by the trigger and resume entry points so a host restart can continue
    /// from the recorded point with forwarded values intact.
    /// </param>
    /// <param name="resumeGateProbe">
    /// Optional resume idempotency probe for approval gates. <c>null</c> on the trigger path (no prior
    /// decision exists yet, so the gate arm is byte-for-byte); bound by the resume entry point so a gate
    /// already decided before a crash is honored instead of re-opened.
    /// </param>
    /// <param name="resumeShadowProbe">
    /// Optional resume idempotency probe for cross-service steps, threaded into the shared
    /// <see cref="CrossServiceStepInvoker"/>. <c>null</c> on the trigger path (first-pass dispatch
    /// unchanged); bound by the resume entry point so a cross-service step that already terminated before a
    /// crash is not re-dispatched.
    /// </param>
    public BatchExecutor(
        IJobRunnerInternal runner,
        IApprovalGateCoordinator approvalCoordinator,
        IJobExecutionAwaiter awaiter,
        ITransport transport,
        string? thisServiceName,
        TimeProvider timeProvider,
        ILogger<BatchExecutor> logger,
        Func<int, IReadOnlyDictionary<string, object?>, CancellationToken, Task>? onStepCompleted = null,
        IResumeGateProbe? resumeGateProbe = null,
        IResumeShadowProbe? resumeShadowProbe = null)
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
        _resumeShadowProbe = resumeShadowProbe;   // null in the byte-for-byte trigger path
        _resumeGateProbe = resumeGateProbe;       // null in the byte-for-byte trigger path
        _crossServiceInvoker = CrossServiceStepInvoker.Create(transport, runner, thisServiceName, timeProvider, resumeShadowProbe);
        _onStepCompleted = onStepCompleted;   // null in the byte-for-byte trigger path
        _logger = logger;
    }

    /// <summary>
    /// Runs a batch definition end-to-end. Throws <see cref="OperationCanceledException"/> on
    /// cancellation; otherwise throws <see cref="InvalidOperationException"/> only when
    /// <see cref="BatchFailurePolicy.StopOnFailure"/> or <see cref="BatchFailurePolicy.Compensate"/>
    /// re-throws.
    /// </summary>
    /// <param name="def">The batch definition to run.</param>
    /// <param name="batchId">The batch RUN id (one per run).</param>
    /// <param name="initial">Initial parameters merged into each step's parameters.</param>
    /// <param name="triggeredBy">Identity that triggered the run; <c>null</c> when unattributed.</param>
    /// <param name="cancellationToken">Cancels the run (host shutdown / administrative cancel).</param>
    /// <param name="startStepIndex">
    /// Index into the ordered step sequence to start from. <c>0</c> (the default) runs the whole batch
    /// from the beginning — the byte-for-byte trigger path. A resume passes the recorded cursor so
    /// already-completed steps are skipped.
    /// </param>
    /// <param name="resumeOutputs">
    /// Optional accumulated outputs to seed the forwarding accumulator on resume, so steps after the
    /// resume point still see earlier steps' outputs. <c>null</c> on the trigger path (empty accumulator).
    /// </param>
    public async Task RunAsync(
        BatchDefinition def,
        string batchId,
        JobParameters initial,
        string? triggeredBy,
        CancellationToken cancellationToken,
        int startStepIndex = 0,
        IReadOnlyDictionary<string, object?>? resumeOutputs = null)
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
        // Run-scoped accumulator of step outputs, forwarded into later steps' parameters. Seeded empty
        // on the trigger path; a resume rehydrates it from the persisted forwarded state.
        var accumulatedOutputs = resumeOutputs is { Count: > 0 }
            ? new Dictionary<string, object?>(resumeOutputs, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var i = startStepIndex; i < orderedSteps.Count; i++)
        {
            var step = orderedSteps[i];
            try
            {
                var stepOutputs = await RunStepAsync(def, batchId, step, initial, accumulatedOutputs, triggeredBy, cancellationToken).ConfigureAwait(false);
                if (stepOutputs is { Count: > 0 })
                {
                    foreach (var (k, v) in stepOutputs)
                    {
                        accumulatedOutputs[k] = v;
                    }
                }

                // Persist the resume cursor AFTER the step succeeds (next-to-run = i + 1). Skipped
                // entirely on the trigger path (seam unbound) — zero added work, identical exception
                // surface. Placed inside the try, after the await, so a cursor write only happens on a
                // genuinely completed step; a failed step throws above and never advances the cursor.
                if (_onStepCompleted is not null)
                {
                    await _onStepCompleted(i + 1, BuildForwardedState(initial, accumulatedOutputs), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Batch cancellation (host shutdown / operator cancel) must propagate as-is and must NOT
                // run OnFailureSteps — a cancelled batch is not a failed batch. Ordered ahead of the
                // failure arms so cancellation always wins.
                throw;
            }
            catch (BatchStepFailureException stepFailure)
            {
                var compensation = RouteStepFailureAsync(
                    def, batchId, step, stepFailure, initial, accumulatedOutputs, triggeredBy, cancellationToken,
                    ref firstFailure, out var continueLoop);
                if (compensation is not null)
                {
                    await compensation.ConfigureAwait(false);
                }
                if (continueLoop)
                {
                    continue;
                }
                throw;
            }
            catch (Exception ex)
            {
                // A step raised something other than a step-terminated-as-Failed signal — e.g. an
                // unregistered job name surfacing from dispatch, or an unexpected fault in the job
                // machinery. These are real step failures and must follow the same FailurePolicy routing
                // (including compensation); otherwise the failure policy is silently skipped. Wrap so the
                // policy switch has a single descriptive failure type while preserving the original cause.
                var wrapped = new BatchStepFailureException($"Step {step.StepId} failed: {ex.Message}", ex);
                var compensation = RouteStepFailureAsync(
                    def, batchId, step, wrapped, initial, accumulatedOutputs, triggeredBy, cancellationToken,
                    ref firstFailure, out var continueLoop);
                if (compensation is not null)
                {
                    await compensation.ConfigureAwait(false);
                }
                if (continueLoop)
                {
                    continue;
                }
                throw wrapped;
            }
        }
        _ = firstFailure;
    }

    /// <summary>
    /// Routes a step failure through <see cref="BatchDefinition.FailurePolicy"/>. Returns the
    /// compensation <see cref="Task"/> the caller must await (only for <see cref="BatchFailurePolicy.Compensate"/>
    /// with non-empty <see cref="BatchDefinition.OnFailureSteps"/>), or <c>null</c> otherwise.
    /// <paramref name="continueLoop"/> is <c>true</c> only for <see cref="BatchFailurePolicy.ContinueOnFailure"/>;
    /// every other case leaves it <c>false</c> so the caller rethrows. Failure state is threaded
    /// explicitly (no instance fields) to keep the executor reentrant.
    /// </summary>
    private Task? RouteStepFailureAsync(
        BatchDefinition def,
        string batchId,
        BatchStep step,
        BatchStepFailureException failure,
        JobParameters initial,
        IReadOnlyDictionary<string, object?> accumulatedOutputs,
        string? triggeredBy,
        CancellationToken cancellationToken,
        ref Exception? firstFailure,
        out bool continueLoop)
    {
        continueLoop = false;
        switch (def.FailurePolicy)
        {
            case BatchFailurePolicy.StopOnFailure:
                return null;

            case BatchFailurePolicy.ContinueOnFailure:
                _logger.LogWarning(failure, "Batch {Batch} step {Step} failed; continuing per ContinueOnFailure policy.", batchId, step.StepId);
                firstFailure ??= failure;
                continueLoop = true;
                // The cursor is NOT advanced for the failed step itself (the cursor write lives inside the
                // try, after a successful await, so a throw skips it); it next advances when a LATER step
                // succeeds. This is moot in practice: a ContinueOnFailure run always reaches a terminal
                // status and is never resumed, so its cursor is never read.
                return null;

            case BatchFailurePolicy.Compensate:
                if (def.OnFailureSteps.Count == 0)
                {
                    return null;
                }
                return RunCompensationAsync(def, batchId, initial, accumulatedOutputs, triggeredBy, cancellationToken);

            default:
                return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, object?>?> RunStepAsync(
        BatchDefinition def,
        string batchId,
        BatchStep step,
        JobParameters initial,
        IReadOnlyDictionary<string, object?> accumulatedOutputs,
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

                if (string.IsNullOrWhiteSpace(step.Job.TargetService))
                {
                    // === LOCAL PATH ===
                    // A null, empty, or whitespace TargetService means "run here" — guarding against
                    // whitespace as well as null keeps this consistent with the trigger-time pre-flight.
                    var execId = IdGenerator.NewExecutionId();
                    var waitTask = _awaiter.WaitForTerminalAsync(execId, cancellationToken);
                    try
                    {
                        await _runner.TriggerInternalAsync(
                            step.Job.JobName,
                            ParallelGroupRunner.MergeParameters(initial, accumulatedOutputs, step.Job.Parameters),
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
                    // Forward this step's outputs into the run accumulator.
                    return terminal.Outputs;
                }
                else
                {
                    // === CROSS-SERVICE PATH ===
                    // Sequential semantics: a Failed/Cancelled terminal status, a transport timeout, or a
                    // transport exception throw BatchStepFailureException so the per-step failure routing applies.
                    // The remote step receives forwarded outputs as parameters; its own produced outputs come
                    // back on the reply (Completed only) and fold into the run accumulator like a local step.
                    var result = await _crossServiceInvoker.InvokeAsync(
                        def, batchId, step, initial, accumulatedOutputs, triggeredBy, throwOnFailure: true, cancellationToken).ConfigureAwait(false);
                    return result.Outputs;
                }
            }

            case BatchStepType.ParallelGroup:
                return await ParallelGroupRunner.RunAsync(
                    def, batchId, step, initial, accumulatedOutputs, triggeredBy,
                    _runner, _awaiter,
                    _transport, _thisServiceName, _timeProvider,
                    cancellationToken,
                    _resumeShadowProbe).ConfigureAwait(false);

            case BatchStepType.ApprovalGate:
            {
                if (step.Approval is null)
                {
                    throw new InvalidOperationException($"Step {step.StepId} is ApprovalGate but has no payload.");
                }

                // Resume idempotency: if THIS run already has a decided gate for THIS step (recorded before
                // a crash), honor that decision instead of opening a fresh gate and blocking on a human
                // again. Pending/absent → fall through to the normal await — and on the first pass there is
                // no decided record yet, so the probe (null on the trigger path, empty result on resume)
                // leaves this byte-for-byte.
                if (_resumeGateProbe is not null)
                {
                    var priorOutcome = await _resumeGateProbe
                        .TryGetDecidedOutcomeAsync(batchId, step.StepId, cancellationToken).ConfigureAwait(false);
                    if (priorOutcome is { } outcome)
                    {
                        switch (outcome)
                        {
                            case ApprovalRecordOutcome.Approved:
                            case ApprovalRecordOutcome.AutoApproved:
                                return null;   // already approved → skip the gate, proceed to the next step
                            case ApprovalRecordOutcome.Rejected:
                            case ApprovalRecordOutcome.TimedOutFail:
                            case ApprovalRecordOutcome.Dismissed:
                                // A genuine negative decision (human reject / timeout-fail / legacy dismiss)
                                // fails the step exactly as it failed the original run.
                                throw new BatchStepFailureException(
                                    $"Step {step.StepId} approval gate was decided '{outcome}' on a prior attempt.");
                            default:
                                // Interrupted / Cancelled are crash-orphan markers (reaper-set or torn down),
                                // NOT human decisions. Re-OPEN the gate so a real decision can still be made.
                                await _approvalCoordinator.AwaitApprovalAsync(batchId, step.StepId, step.Approval, def.Name, def.Id, cancellationToken).ConfigureAwait(false);
                                return null;
                        }
                    }

                    // No decided record, but a PENDING gate from a prior attempt may exist (a crash or
                    // graceful shutdown within the reaper grace window left it Pending). RE-ATTACH to that
                    // gate instead of minting a SECOND pending gate — otherwise the operator would see two
                    // identical approvals for one step. null → no pending gate → fall through to the normal
                    // mint (the first-attempt path).
                    var pendingId = await _resumeGateProbe
                        .TryGetPendingApprovalIdAsync(batchId, step.StepId, cancellationToken).ConfigureAwait(false);
                    if (pendingId is { } existingId)
                    {
                        await _approvalCoordinator.ReattachApprovalAsync(
                            existingId, batchId, step.StepId, step.Approval, def.Name, def.Id, cancellationToken).ConfigureAwait(false);
                        return null;
                    }
                }

                await _approvalCoordinator.AwaitApprovalAsync(batchId, step.StepId, step.Approval, def.Name, def.Id, cancellationToken).ConfigureAwait(false);
                return null;
            }

            default:
                _logger.LogWarning("Unknown BatchStepType {Type} on step {StepId}; treating as no-op (forward-compat).", step.StepType, step.StepId);
                return null;
        }
    }

    private async Task RunCompensationAsync(
        BatchDefinition def,
        string batchId,
        JobParameters initial,
        IReadOnlyDictionary<string, object?> accumulatedOutputs,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        foreach (var step in def.OnFailureSteps.OrderBy(s => s.Order))
        {
            try
            {
                // Compensation steps receive forwarded outputs as parameters; their own output is not
                // folded forward (no subsequent normal step follows compensation).
                await RunStepAsync(def, batchId, step, initial, accumulatedOutputs, triggeredBy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compensation step {Step} of batch {Batch} failed; continuing remaining compensation.", step.StepId, batchId);
                // do NOT rethrow — acyclic safety; we do not compensate the compensation.
            }
        }
    }

    /// <summary>
    /// Builds the run's forwarded-state payload persisted after each step: the batch-initial parameters
    /// and a snapshot of the accumulated outputs, under reserved <c>ukbatch.*</c> keys, so a resume can
    /// rehydrate both.
    /// </summary>
    private static Dictionary<string, object?> BuildForwardedState(
        JobParameters initial, IReadOnlyDictionary<string, object?> accumulated)
        => new(StringComparer.Ordinal)
        {
            [ForwardedStateKeys.InitialParameters] = initial.Values,
            [ForwardedStateKeys.ForwardedOutputs] = new Dictionary<string, object?>(accumulated, StringComparer.Ordinal),
        };
}
