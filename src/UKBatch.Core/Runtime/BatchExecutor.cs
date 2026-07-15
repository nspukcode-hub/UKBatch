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
    private readonly Func<int, CancellationToken, Task>? _onCompensationProgress;
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
    /// <param name="onCompensationProgress">
    /// Optional durable-unwind seam, invoked with the reverse-unwind cursor as compensation progresses so a
    /// host restart can continue the unwind from where it stopped. <c>null</c> on paths that record no
    /// progress; bound by the trigger and resume entry points. Unbound leaves compensation behavior
    /// identical, just non-durable.
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
        IResumeShadowProbe? resumeShadowProbe = null,
        Func<int, CancellationToken, Task>? onCompensationProgress = null)
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
        _onCompensationProgress = onCompensationProgress;   // null on paths that record no unwind progress
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
        // Indices skipped by an unmet run-if condition. Threaded into a Compensate-policy unwind so a
        // skipped step (which never ran) is never compensated. Empty on the trigger path (feature inert). On a
        // resume-forward (startStepIndex > 0) it is rehydrated from the durable Skipped rows — a step skipped
        // on the ORIGINAL attempt lives only in the store, and the unwind walks the full prior range, so
        // without this a resumed run would wrongly compensate a step that never ran. The probe is null on the
        // trigger path, so this stays a no-op (empty set) there and on stores that cannot record skips.
        var skippedIndices = startStepIndex > 0
            ? new HashSet<int>(await ResolveSkippedIndicesAsync(batchId, orderedSteps, cancellationToken).ConfigureAwait(false))
            : new HashSet<int>();

        for (var i = startStepIndex; i < orderedSteps.Count; i++)
        {
            var step = orderedSteps[i];
            try
            {
                // Run-if guard: evaluate the step's condition against the same parameters it would receive at
                // dispatch (initial + forwarded outputs + the step's own static params). When it does not
                // hold, record a Skipped row, advance the resume cursor, and move on — the step never runs,
                // produces no output, and leaves the accumulator untouched. A step with no condition takes
                // none of this branch (the path stays byte-for-byte).
                if (step.Condition is not null &&
                    !StepConditionEvaluator.Evaluate(
                        step.Condition,
                        ParallelGroupRunner.MergeParameters(initial, accumulatedOutputs, StepParametersOf(step))))
                {
                    skippedIndices.Add(i);
                    await _runner.RecordSkippedStepAsync(batchId, step, def.Id, triggeredBy, cancellationToken).ConfigureAwait(false);
                    if (_onStepCompleted is not null)
                    {
                        await _onStepCompleted(i + 1, BuildForwardedState(initial, accumulatedOutputs), cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

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
                    def, batchId, step, stepFailure, initial, accumulatedOutputs, triggeredBy, orderedSteps, i, skippedIndices, cancellationToken,
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
                    def, batchId, step, wrapped, initial, accumulatedOutputs, triggeredBy, orderedSteps, i, skippedIndices, cancellationToken,
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
    /// when a completed earlier step carries a compensator or <see cref="BatchDefinition.OnFailureSteps"/>
    /// is non-empty), or <c>null</c> otherwise.
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
        List<BatchStep> orderedSteps,
        int failedStepIndex,
        IReadOnlySet<int> skippedIndices,
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
                // Compensation fires when EITHER a completed earlier step [0, failedStepIndex) carries a
                // per-step compensator OR the batch-level failure chain is non-empty; with neither, the
                // route degrades to StopOnFailure — bit-identical to a no-compensator, no-chain definition.
                var hasCompensator = false;
                for (var j = 0; j < failedStepIndex; j++)
                {
                    if (orderedSteps[j].Compensation is not null) { hasCompensator = true; break; }
                }
                if (!hasCompensator && def.OnFailureSteps.Count == 0)
                {
                    return null;   // nothing to compensate AND no failure chain → behaves exactly like StopOnFailure
                }
                return RunCompensationAsync(
                    def, batchId, initial, accumulatedOutputs, triggeredBy, orderedSteps, failedStepIndex, skippedIndices, cancellationToken);

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

            case BatchStepType.Decision:
                return await DecisionStepRunner.RunAsync(
                    def, batchId, step, initial, accumulatedOutputs, triggeredBy,
                    _runner, _awaiter,
                    _transport, _thisServiceName, _timeProvider,
                    _logger, cancellationToken,
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

    /// <summary>
    /// Orchestrates the failure response of a <see cref="BatchFailurePolicy.Compensate"/> batch: mark the
    /// unwind started, run the per-step compensators of completed steps in REVERSE order, mark the unwind
    /// finished, then run the batch-level <see cref="BatchDefinition.OnFailureSteps"/> failure chain.
    /// </summary>
    private async Task RunCompensationAsync(
        BatchDefinition def,
        string batchId,
        JobParameters initial,
        IReadOnlyDictionary<string, object?> accumulatedOutputs,
        string? triggeredBy,
        List<BatchStep> orderedSteps,
        int failedStepIndex,
        IReadOnlySet<int> skippedIndices,
        CancellationToken cancellationToken)
    {
        // Mark the unwind as started BEFORE the first compensator, at the failed step's index. On a crash
        // before any compensator ran, recovery resumes the whole unwind [0, failedStepIndex).
        if (_onCompensationProgress is not null)
        {
            await _onCompensationProgress(failedStepIndex, cancellationToken).ConfigureAwait(false);
        }

        await UnwindRangeAsync(
            def, batchId, initial, accumulatedOutputs, triggeredBy, orderedSteps, failedStepIndex, skippedIndices, cancellationToken)
            .ConfigureAwait(false);

        // Unwind finished → chain phase. (No-op cursor write when the seam is unbound.)
        if (_onCompensationProgress is not null)
        {
            await _onCompensationProgress(0, cancellationToken).ConfigureAwait(false);
        }

        await RunFailureChainAsync(def, batchId, initial, accumulatedOutputs, triggeredBy, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a run that was interrupted mid-unwind: continues the reverse unwind over
    /// <c>[0, compensationStepIndex)</c> (skipping compensators whose derived-id execution row already
    /// completed on a prior attempt), then runs the failure chain. Success does NOT throw — the caller
    /// stamps the run's terminal status (a compensated run is Failed). A <paramref name="compensationStepIndex"/>
    /// of <c>0</c> skips the unwind and runs the chain wholesale.
    /// </summary>
    public async Task ResumeCompensationAsync(
        BatchDefinition def,
        string batchId,
        JobParameters initial,
        IReadOnlyDictionary<string, object?>? resumeOutputs,
        string? triggeredBy,
        int compensationStepIndex,
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
        var accumulatedOutputs = resumeOutputs is { Count: > 0 }
            ? new Dictionary<string, object?>(resumeOutputs, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        // compensationStepIndex is clamped to the ordered-step count by the caller; guard here too.
        var k = Math.Clamp(compensationStepIndex, 0, orderedSteps.Count);
        if (k > 0)
        {
            // A fresh unwind carries its skipped indices in memory; a resumed one rebuilds them from the
            // durable Skipped rows, so a step skipped by an unmet condition on the original run is still
            // excluded from compensation. Empty when nothing was skipped (or no probe is bound).
            var skippedIndices = await ResolveSkippedIndicesAsync(batchId, orderedSteps, cancellationToken).ConfigureAwait(false);
            await UnwindRangeAsync(def, batchId, initial, accumulatedOutputs, triggeredBy, orderedSteps, k, skippedIndices, cancellationToken)
                .ConfigureAwait(false);
            if (_onCompensationProgress is not null)
            {
                await _onCompensationProgress(0, cancellationToken).ConfigureAwait(false);
            }
        }

        await RunFailureChainAsync(def, batchId, initial, accumulatedOutputs, triggeredBy, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The single reverse-unwind core shared by the fresh unwind (<paramref name="fromExclusive"/> = the
    /// failed step's index) and the resumed unwind (<paramref name="fromExclusive"/> = the persisted
    /// compensation cursor). Walks <c>[fromExclusive-1 .. 0]</c>, skipping steps with no compensator (no
    /// row, no cursor write). Each compensator runs through the SHARED <see cref="RunStepAsync"/> via a
    /// synthesized step, inheriting the local awaiter / cross-service shadow / parameter merge / retries /
    /// timeout; its output is NOT forwarded (no subsequent normal step follows). On a resume (shadow probe
    /// bound) a compensator whose derived-id row already completed is skipped — the effectively-once dedupe.
    /// </summary>
    private async Task UnwindRangeAsync(
        BatchDefinition def,
        string batchId,
        JobParameters initial,
        IReadOnlyDictionary<string, object?> accumulatedOutputs,
        string? triggeredBy,
        List<BatchStep> orderedSteps,
        int fromExclusive,
        IReadOnlySet<int> skippedIndices,
        CancellationToken cancellationToken)
    {
        for (var j = fromExclusive - 1; j >= 0; j--)
        {
            var parent = orderedSteps[j];
            if (parent.Compensation is null || skippedIndices.Contains(j))
            {
                // No compensator, OR the step was skipped by an unmet run-if condition — a skipped step never
                // ran, so there is nothing to undo (no row, no cursor write).
                continue;
            }

            var compensatorStep = BuildCompensatorStep(parent);

            // Resume-only dedupe: the probe is null on the trigger path (fresh unwind unchanged) and bound
            // on the resume path, where a compensator whose derived-id row already COMPLETED before the
            // crash must not run twice. Local and cross-service compensators dedupe through the same query.
            if (_resumeShadowProbe is not null)
            {
                var prior = await _resumeShadowProbe
                    .TryGetCompletedStatusAsync(batchId, compensatorStep.StepId, cancellationToken).ConfigureAwait(false);
                if (prior is not null)
                {
                    _logger.LogInformation(
                        "Compensator {Step} of batch {Batch} already completed on a prior attempt; skipping.",
                        compensatorStep.StepId, batchId);
                    if (_onCompensationProgress is not null)
                    {
                        await _onCompensationProgress(j, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }
            }

            try
            {
                await RunStepAsync(def, batchId, compensatorStep, initial, accumulatedOutputs, triggeredBy, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;   // administrative cancel stops the unwind; host shutdown is left in-flight upstream
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Compensator for step {Step} of batch {Batch} failed; continuing the remaining unwind.",
                    parent.StepId, batchId);
            }

            // Cursor AFTER the compensator (a crash before this write re-runs this compensator on resume —
            // the documented at-least-once replay, deduped by the completed-row probe). The reverse order
            // would permanently skip a compensator whose write landed but whose run had not, breaking the
            // saga guarantee.
            if (_onCompensationProgress is not null)
            {
                await _onCompensationProgress(j, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Synthesizes the runnable compensator step for <paramref name="parent"/>. The derived StepId
    /// (<see cref="CompensationStepIds.For"/>) stamps the compensator's execution row, feeding the dashboard
    /// node map, the cross-service shadow, and the resume dedupe probe with no extra plumbing.
    /// </summary>
    private static BatchStep BuildCompensatorStep(BatchStep parent)
    {
        var c = parent.Compensation!;
        return new BatchStep
        {
            StepId = CompensationStepIds.For(parent.StepId),
            Order = parent.Order,
            StepType = BatchStepType.Job,
            Job = new JobStepData
            {
                JobName = c.JobName,
                TargetService = c.TargetService,
                Parameters = c.Parameters,
                MaxRetries = c.MaxRetries,
                TimeoutSeconds = c.TimeoutSeconds,
            },
            ParallelGroup = null,
            Approval = null,
            Metadata = null,
        };
    }

    /// <summary>
    /// Runs the batch-level <see cref="BatchDefinition.OnFailureSteps"/> failure chain (forward order).
    /// Cancellation rethrows so an administrative cancel ends the run Cancelled and a host shutdown leaves
    /// it in-flight for durable resume; any other failure is logged and the chain continues (best-effort —
    /// there is no compensation of compensation).
    /// </summary>
    private async Task RunFailureChainAsync(
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
                // Chain steps receive forwarded outputs as parameters; their own output is not
                // folded forward (no subsequent normal step follows compensation).
                await RunStepAsync(def, batchId, step, initial, accumulatedOutputs, triggeredBy, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;   // cancellation stops the chain (matches the unwind arm); no compensation of compensation
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Compensation chain step {Step} of batch {Batch} failed; continuing remaining chain.", step.StepId, batchId);
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

    /// <summary>Shared empty skipped-index set for the common no-conditions path (avoids a per-call allocation).</summary>
    private static readonly IReadOnlySet<int> EmptyIndexSet = new HashSet<int>();

    /// <summary>
    /// The static parameters a step carries into its run-if evaluation. Only a Job step has its own static
    /// parameters; a ParallelGroup or ApprovalGate contributes none, so the condition sees just the initial
    /// parameters plus the forwarded outputs — the same data the step would receive at dispatch.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? StepParametersOf(BatchStep step) =>
        step.StepType == BatchStepType.Job ? step.Job?.Parameters : null;

    /// <summary>
    /// Rebuilds the skipped-index set for a resumed unwind by mapping the run's durable
    /// <see cref="JobStatus.Skipped"/> step rows back to their ordered-step positions. Returns an empty set
    /// when no resume probe is bound or nothing was skipped.
    /// </summary>
    private async Task<IReadOnlySet<int>> ResolveSkippedIndicesAsync(
        string batchId, List<BatchStep> orderedSteps, CancellationToken cancellationToken)
    {
        if (_resumeShadowProbe is null)
        {
            return EmptyIndexSet;
        }
        var skippedStepIds = await _resumeShadowProbe.GetSkippedStepIdsAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (skippedStepIds.Count == 0)
        {
            return EmptyIndexSet;
        }
        var indices = new HashSet<int>();
        for (var i = 0; i < orderedSteps.Count; i++)
        {
            if (skippedStepIds.Contains(orderedSteps[i].StepId))
            {
                indices.Add(i);
            }
        }
        return indices;
    }
}
