using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.Internal;
using UKBatch.Registry;

namespace UKBatch.Runtime;

/// <summary>
/// Concrete implementation of both <see cref="IJobRunner"/> (public) and
/// <see cref="IJobRunnerInternal"/> (internal). An ISP split — the same singleton backs
/// both interfaces.
/// </summary>
internal sealed class JobRunner : IJobRunner, IJobRunnerInternal
{
    /// <summary>
    /// Per-process one-shot guard so the "adapter does not implement InsertAsync" warning emits
    /// ONCE per adapter type per process. Without this, EF/Redis adapter authors who forget the
    /// InsertAsync overload would generate N warnings per N executions (100-step partitioned
    /// batches × 100 warnings) — alert flood on Crashlytics-class sinks. The static field is
    /// intentional: the warning is a process-wide diagnostic, not per-host. Test-only reset hook:
    /// <see cref="ResetWarnedAdapterTypesForTesting"/>.
    /// </summary>
    private static readonly HashSet<string> _warnedAdapterTypes = new(StringComparer.Ordinal);
    private static readonly object _warnLock = new();

    /// <summary>
    /// Test-only reset hook for <see cref="_warnedAdapterTypes"/>. Friend-accessible to
    /// <c>UKBatch.Core.Tests</c> via <c>InternalsVisibleTo</c>. Not part of the public API.
    /// </summary>
    internal static void ResetWarnedAdapterTypesForTesting()
    {
        lock (_warnLock) { _warnedAdapterTypes.Clear(); }
    }

    private readonly JobDefinitionRegistry _jobRegistry;
    private readonly IBatchDefinitionLookup _batchLookup;
    private readonly IJobStore _jobStore;
    private readonly IBatchDefinitionStore _batchDefinitionStore;
    private readonly JobDispatcher _dispatcher;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _clock;
    private readonly IOptions<UKBatchOptions> _options;
    private readonly ITransport _transport;
    private readonly BatchCompletionSignal _batchCompletionSignal;
    private readonly IBatchRunStore _batchRunStore;
    private readonly BatchRunRegistry _batchRunRegistry;
    private readonly ILogger<JobRunner> _logger;
    private readonly CancellationToken _hostStopping;

    /// <summary>Constructs the runner.</summary>
    public JobRunner(
        JobDefinitionRegistry jobRegistry,
        IBatchDefinitionLookup batchLookup,
        IJobStore jobStore,
        IBatchDefinitionStore batchDefinitionStore,
        JobDispatcher dispatcher,
        IServiceProvider serviceProvider,
        TimeProvider clock,
        IOptions<UKBatchOptions> options,
        IHostApplicationLifetime hostLifetime,
        BatchCompletionSignal batchCompletionSignal,
        ITransport transport,
        IBatchRunStore batchRunStore,
        BatchRunRegistry batchRunRegistry,
        ILogger<JobRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(jobRegistry);
        ArgumentNullException.ThrowIfNull(batchLookup);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(batchDefinitionStore);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostLifetime);
        ArgumentNullException.ThrowIfNull(batchCompletionSignal);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(batchRunStore);
        ArgumentNullException.ThrowIfNull(batchRunRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        _jobRegistry = jobRegistry;
        _batchLookup = batchLookup;
        _jobStore = jobStore;
        _batchDefinitionStore = batchDefinitionStore;
        _dispatcher = dispatcher;
        _serviceProvider = serviceProvider;
        _clock = clock;
        _options = options;
        _transport = transport;
        _batchCompletionSignal = batchCompletionSignal;
        _batchRunStore = batchRunStore;
        _batchRunRegistry = batchRunRegistry;
        _hostStopping = hostLifetime.ApplicationStopping;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the host's service identity for outbound
    /// <see cref="JobMessage.SourceService"/>. Resolution order: <see cref="UKBatchOptions.ThisServiceName"/>
    /// → env var <c>UKBATCH_SERVICE_NAME</c> → <c>Assembly.GetEntryAssembly()?.GetName().Name</c>.
    /// Returns <c>null</c> when none resolve to a non-empty string (receiver-only nodes).
    /// </summary>
    private string? ResolveThisServiceName()
    {
        var fromConfig = _options.Value.ThisServiceName;
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }
        var fromEnv = Environment.GetEnvironmentVariable("UKBATCH_SERVICE_NAME");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }
        return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
    }

    // ===== IJobRunner =====

    /// <inheritdoc/>
    public Task<JobExecution> TriggerAsync(
        string jobName,
        JobParameters parameters,
        string? triggeredBy,
        CancellationToken cancellationToken)
        => TriggerInternalAsync(
            jobName,
            parameters,
            triggeredBy,
            batchId: null,
            stepId: null,
            predefinedExecutionId: null,
            batchDefinitionId: null,
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// The batch runs to completion (or host shutdown) regardless of the
    /// caller's <paramref name="cancellationToken"/>. Cancellation of <paramref name="cancellationToken"/>
    /// does NOT cancel the batch — the caller's CT bounds the TRIGGER CALL (definition lookup, etc.)
    /// only, NOT the batch lifetime. REST endpoints can safely pass
    /// <c>HttpContext.RequestAborted</c> without tying the batch to the HTTP request.
    /// The batch's effective cancellation token is <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
    /// <para>
    /// <b>Resolution order:</b> code-defined registry first (by id via
    /// <c>IBatchDefinitionLookup.TryGetById</c>), then store-defined batches (by id via
    /// <c>IBatchDefinitionStore.GetAsync</c>). On hypothetical id collision (UUIDv7 vs
    /// caller-supplied store id), the code-defined batch wins silently. For name-keyed routing,
    /// callers compose <c>IBatchDefinitionLookup.TryGetByName</c> (Code) with
    /// <c>IBatchDefinitionStore.GetByNameAsync</c> (Dashboard/Api) and pass the resolved id here.
    /// </para>
    /// </remarks>
    public async Task<string> TriggerBatchAsync(
        string batchDefinitionId,
        JobParameters? initialParameters,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);
        var def = _batchLookup.TryGetById(batchDefinitionId)
            ?? await _batchDefinitionStore.GetAsync(batchDefinitionId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchDefinitionNotFoundException($"BatchDefinition {batchDefinitionId} not found.") { BatchDefinitionId = batchDefinitionId };

        // Synchronous pre-flight: surface validation / unregistered-job errors to the caller (HTTP 400)
        // instead of accepting the trigger and producing zero executions. Real RUNTIME job failures
        // (a job that throws at execution) stay async by design.
        ValidateBatchForTrigger(def);

        var batchId = IdGenerator.NewBatchId();

        // Persist the run record on the trigger thread, BEFORE the fire-and-forget run starts, so a
        // store failure surfaces on the caller's path (consistent with the synchronous pre-flight). The
        // run is created in-progress (Status null, counters 0); StepCount is the definition's step count.
        var stepCount = CountDefinitionSteps(def);
        await _batchRunStore.CreateAsync(
            new BatchRun
            {
                BatchId = batchId,
                BatchDefinitionId = def.Id,
                BatchName = def.Name,
                Status = null,
                TriggeredBy = triggeredBy,
                StartedAtUtc = _clock.GetUtcNow(),
                CompletedAtUtc = null,
                StepCount = stepCount,
                Total = 0,
                Succeeded = 0,
                Failed = 0,
                Cancelled = 0,
                // Persist the batch-initial parameters up front so a crash BEFORE the first step completes
                // still resumes with them. Each completed step then overwrites this with the full forwarded
                // state (initial parameters + accumulated outputs).
                ForwardedState = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ForwardedStateKeys.InitialParameters] = (initialParameters ?? JobParameters.Empty).Values,
                },
            },
            cancellationToken).ConfigureAwait(false);

        var executor = new BatchExecutor(
            this,
            _serviceProvider.GetRequiredService<IApprovalGateCoordinator>(),
            _serviceProvider.GetRequiredService<IJobExecutionAwaiter>(),
            _transport,
            ResolveThisServiceName(),
            _clock,
            _serviceProvider.GetRequiredService<ILogger<BatchExecutor>>(),
            // After each completed step, persist BOTH the run's forwarded state (batch-initial parameters +
            // accumulated outputs) AND the resume cursor, identical to the resume path. This is additive:
            // dispatch / completion / signal behavior is unchanged (separate fields). The cursor is what
            // makes a crash mid-run recoverable — without it CurrentStepIndex stays null and recovery's
            // ResumeForward would restart from the beginning, re-running a completed step (e.g. a payment);
            // without the forwarded state a resume would lose earlier steps' outputs.
            //
            // Order matters: forwarded state is written FIRST, cursor SECOND, in two separate transactions.
            // A crash between them can then only leave forwarded-state ahead of the cursor, so the just-
            // completed step re-runs on resume (the documented ResumeForward worst case) and re-produces its
            // output. The reverse order could advance the cursor past a step whose output was not yet
            // persisted, silently dropping a forwarded value. On the in-memory store these writes are
            // harmless and lost on restart, the documented in-memory durability boundary.
            onStepCompleted: async (nextIndex, forwardedState, ct) =>
            {
                await _batchRunStore.UpdateForwardedStateAsync(batchId, forwardedState, ct).ConfigureAwait(false);
                await _batchRunStore.UpdateCursorAsync(batchId, nextIndex, ct).ConfigureAwait(false);
            });

        // Per-run cancellation: link the host-stopping token with a fresh source so an administrative
        // cancel (via IBatchRunCanceller) trips ONLY this run; the host token still cancels every run on
        // shutdown. The executor receives this linked token instead of the bare host token. Register the
        // source immediately before the fire-and-forget so nothing can throw between registration and the
        // Task.Run that owns its disposal.
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_hostStopping);
        _batchRunRegistry.Register(batchId, runCts);
        var runToken = runCts.Token;

        // Fire-and-forget against the HOST's lifetime + this run's cancel source, NOT the caller's CT.
        _ = Task.Run(async () =>
        {
            // Capture the runtime's terminal verdict so the hub fan-out can override the row-derived
            // aggregate. A failing approval gate (rejected / dismissed / timed-out-Fail) rethrows out
            // of RunAsync but leaves NO JobExecution row, so without this the row aggregate would
            // report the run Completed (green) even though the batch ended in failure.
            JobStatus? runtimeTerminal = null;
            // Distinguishes a graceful host shutdown from an administrative cancel — see the OCE catch.
            var hostShuttingDown = false;
            try
            {
                await executor.RunAsync(def, batchId, initialParameters ?? JobParameters.Empty, triggeredBy, runToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // The run token tripped. The parent host-stopping token distinguishes WHY: cancelling
                // the parent cancels every run's linked token at once, while an administrative cancel
                // (IBatchRunCanceller) trips only this run's child source and never propagates up. So:
                //   - host shutdown (parent cancelled) → leave the run in-flight (do not finalize). The
                //     resume cursor already points at the last completed step, so recovery continues
                //     from exactly the right place on the next start. A run that races a shutdown
                //     survives; re-cancel after restart if genuinely unwanted.
                //   - administrative cancel (parent NOT cancelled) → a deliberate kill; record Cancelled.
                // Must precede the general Exception catch (subtype) and logs at Warning, not Error.
                if (_hostStopping.IsCancellationRequested)
                {
                    hostShuttingDown = true;
                    _logger.LogInformation(ex,
                        "Batch {BatchId} (definition {DefId}) interrupted by host shutdown; left in-flight for durable resume.",
                        batchId, batchDefinitionId);
                }
                else
                {
                    runtimeTerminal = JobStatus.Cancelled;
                    _logger.LogWarning(ex, "Batch {BatchId} (definition {DefId}) cancelled.", batchId, batchDefinitionId);
                }
            }
            catch (Exception ex)
            {
                runtimeTerminal = JobStatus.Failed;
                _logger.LogError(ex, "Batch {BatchId} (definition {DefId}) failed.", batchId, batchDefinitionId);
            }
            finally
            {
                // Finalize the run record from the runtime verdict + a single execution-count query. This
                // is the data-layer source of truth for the run's terminal status (a gate-failed run
                // leaves no execution row, so a pure roll-up would read Completed). Independent of the
                // SignalR signal below.
                //
                // SKIPPED on graceful host shutdown: the run stays in-progress (Status null) so the next
                // host's recovery sees it as in-flight and resumes it, rather than reading a terminal
                // Cancelled record and skipping it. Everything else in this finally still runs — the CTS
                // belongs to this closure and must be disposed even when the run is left in-flight.
                if (!hostShuttingDown)
                {
                    await CompleteRunRecordAsync(batchId, runtimeTerminal).ConfigureAwait(false);
                }

                // De-register + dispose THIS run's cancel source. The registry only ever calls Cancel();
                // ownership of disposal is here. Remove first (so a late Cancel misses the lookup), then
                // dispose.
                _batchRunRegistry.Remove(batchId);
                runCts.Dispose();

                // Signal the hub fan-out that the batch run has finished. The payload carries
                // (BatchRunId, BatchDefinitionId, BatchName) so the hub fan-out can populate
                // BatchCompletionSummary.BatchDefinitionId without an IBatchCatalogService roundtrip.
                // RuntimeTerminalStatus carries the closure's verdict (null on clean completion AND on
                // host shutdown — the in-process run is over on this node; the durable Status stays null).
                // The hub queries the store ONCE per signal to compute the aggregate shard counts. This
                // path is unchanged from before the run-store existed — the two terminal side-effects are
                // independent.
                _batchCompletionSignal.Signal(new BatchCompletionSignalPayload
                {
                    BatchRunId = batchId,
                    BatchDefinitionId = def.Id,
                    BatchName = def.Name,
                    RuntimeTerminalStatus = runtimeTerminal,
                });
            }
        }, CancellationToken.None);

        return batchId;
    }

    /// <summary>
    /// Synchronous pre-flight run by <see cref="TriggerBatchAsync"/> before the fire-and-forget run:
    /// structural validation plus a local job-registration check, throwing
    /// <see cref="BatchTriggerValidationException"/> so a trigger endpoint can return 400 instead of
    /// accepting a trigger that would produce zero executions. Cross-service steps are skipped here
    /// (the target job lives on a remote worker, not in this process's registry).
    /// </summary>
    private void ValidateBatchForTrigger(BatchDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var errors = new List<BatchTriggerValidationError>();

        var structural = Validation.BatchDefinitionValidator.Validate(def);
        foreach (var e in structural.Errors)
        {
            errors.Add(new BatchTriggerValidationError(e.PropertyPath, e.Message));
        }

        // Local job-name registration check; cross-service steps are remote jobs and are not in
        // this process's registry, so they are deliberately not checked here.
        foreach (var step in EnumerateJobSteps(def))
        {
            if (step.Job is { JobName: { Length: > 0 } name }
                && string.IsNullOrWhiteSpace(step.Job.TargetService)
                && _jobRegistry.TryGet(name) is null)
            {
                errors.Add(new BatchTriggerValidationError(
                    $"step '{step.StepId}'", $"job '{name}' is not registered"));
            }
        }

        if (errors.Count > 0)
        {
            throw new BatchTriggerValidationException(
                $"Batch definition '{def.Id}' cannot be triggered: {errors.Count} error(s).", errors);
        }
    }

    /// <summary>
    /// Walks every Job-bearing step of a definition: the main <see cref="BatchDefinition.Steps"/>,
    /// the children of each <see cref="BatchStepType.ParallelGroup"/>, and the
    /// <see cref="BatchDefinition.OnFailureSteps"/> compensation chain.
    /// </summary>
    private static IEnumerable<BatchStep> EnumerateJobSteps(BatchDefinition def)
    {
        foreach (var step in def.Steps)
        {
            yield return step;
            if (step.ParallelGroup is { Steps: { } children })
            {
                foreach (var child in children)
                {
                    yield return child;
                }
            }
        }
        foreach (var step in def.OnFailureSteps)
        {
            yield return step;
        }
    }

    /// <summary>
    /// Completes the run record from the runtime's terminal verdict and a single execution-count query.
    /// Mirrors the hub fan-out roll-up: counts are row-derived; the run's STATUS prefers the runtime
    /// verdict (a gate failure leaves no execution row, so a pure roll-up would read Completed).
    /// CT-decoupled (CancellationToken.None) and exception-swallowing: the cancel path has the run token
    /// already tripped, and the finally must never crash on a store/dispose hiccup at shutdown.
    /// </summary>
    private async Task CompleteRunRecordAsync(string batchId, JobStatus? runtimeTerminal)
    {
        try
        {
            var executions = await _jobStore.QueryAsync(
                new JobQuery { BatchId = batchId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None).ConfigureAwait(false);

            // Status tallies count the LATEST attempt per batch step, so a step re-run by a resume
            // contributes only its final outcome and an earlier interrupted attempt (tombstoned to Failed
            // by the orphan reaper) is not double-counted. For a run that was never resumed every step has
            // exactly one row, so this collapse is the identity function and the counts are bit-identical
            // to a flat count. Total keeps the flat row count — an honest "how many rows exist" audit
            // number that includes the dead orphan, which genuinely happened.
            var latestPerStep = LatestAttemptPerStep(executions);
            var succeeded = latestPerStep.Count(e => e.Status == JobStatus.Completed);
            var failed = latestPerStep.Count(e => e.Status == JobStatus.Failed);
            var cancelled = latestPerStep.Count(e => e.Status == JobStatus.Cancelled);

            var rowAggregate = cancelled > 0 ? JobStatus.Cancelled
                             : failed > 0 ? JobStatus.Failed
                             : JobStatus.Completed;
            var terminal = runtimeTerminal ?? rowAggregate;

            await _batchRunStore.CompleteAsync(
                batchId,
                terminal,
                new BatchRunCounts(executions.Count, succeeded, failed, cancelled),
                _clock.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Host shutting down — a pooled EF context factory may throw at disposal. Best-effort; don't
            // surface a scary dispose trace from the fire-and-forget finally.
        }
        catch (Exception ex)
        {
            // Run completion is observability, not control flow — never let it crash the closure.
            _logger.LogWarning(ex, "Could not finalize batch run record {BatchId}.", batchId);
        }
    }

    /// <summary>
    /// Collapses a run's execution rows to the latest attempt of each batch step, so a step re-run by a
    /// resume contributes only its final outcome and an earlier interrupted attempt (later tombstoned to
    /// Failed by the orphan reaper) is not double-counted. For a run that was never resumed every step has
    /// exactly one row, so this returns the input set unchanged — the non-resume aggregate is bit-identical
    /// to a flat count. Rows with a null step id (defensive; batch rows always carry one) are kept as
    /// distinct singletons keyed by execution id, so a hypothetical null never collapses two unrelated rows.
    /// </summary>
    /// <remarks>
    /// "Latest" orders by <see cref="JobExecution.EnqueuedAtUtc"/> descending, tiebroken by
    /// <see cref="JobExecution.ExecutionId"/> descending. Execution ids are UUIDv7 "N" hex (time-ordered),
    /// so the re-run (enqueued later) wins, and the id tiebreak is the deterministic backstop when two
    /// share a timestamp.
    /// </remarks>
    private static List<JobExecution> LatestAttemptPerStep(IReadOnlyList<JobExecution> rows)
        => rows
            .GroupBy(e => e.BatchStepId ?? ("\0exec:" + e.ExecutionId))
            .Select(g => g.OrderByDescending(e => e.EnqueuedAtUtc)
                          .ThenByDescending(e => e.ExecutionId, StringComparer.Ordinal)
                          .First())
            .ToList();

    /// <summary>
    /// Counts the steps of a definition for the run record's <c>StepCount</c>: each Job step, each
    /// ParallelGroup CHILD (the group is a grouping, not a step in its own right), each ApprovalGate
    /// step, and each OnFailureSteps compensation step. A topology number, distinct from the
    /// executed-row total.
    /// </summary>
    private static int CountDefinitionSteps(BatchDefinition def)
    {
        var count = 0;
        foreach (var step in def.Steps)
        {
            count += step.ParallelGroup is { Steps: { } children } ? children.Count : 1;
        }
        return count + def.OnFailureSteps.Count;
    }

    /// <inheritdoc/>
    public async Task CancelAsync(string executionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        var current = await _jobStore.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            // Typed exception so the cancel endpoint can map to 404.
            throw new JobExecutionNotFoundException($"Execution {executionId} not found.") { ExecutionId = executionId };
        }
        if (BatchStateMachine.IsTerminal(current.Status))
        {
            return;
        }
        // Move toward Cancelling; the worker observes a CT and finalises as Cancelled. We do NOT
        // attempt a direct Cancelling -> Cancelled here because the worker owns the in-flight work.
        if (BatchStateMachine.CanTransition(current.Status, JobStatus.Cancelling))
        {
            await _jobStore.UpdateStatusAsync(executionId, JobStatus.Cancelling, "user cancelled", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task ResumeBatchAsync(string batchId, ResumePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);

        var run = await _batchRunStore.GetAsync(batchId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchRunNotFoundException($"Batch run {batchId} not found.") { BatchId = batchId };

        // Already terminal → nothing to resume (idempotent: a duplicate recovery call is a no-op).
        if (run.Status is not null)
        {
            return;
        }

        // Resolve the definition by the run's DEFINITION id (creation-time topology). Code-defined
        // registry first, then the store, mirroring TriggerBatchAsync's resolution order.
        var def = _batchLookup.TryGetById(run.BatchDefinitionId)
            ?? await _batchDefinitionStore.GetAsync(run.BatchDefinitionId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchDefinitionNotFoundException(
                $"Definition {run.BatchDefinitionId} for run {batchId} not found; cannot resume.")
            { BatchDefinitionId = run.BatchDefinitionId };

        // Definition-drift tripwire: the cursor was recorded against the run's creation-time topology.
        // If the definition's step count changed since (a step was added/removed), a forward replay could
        // skip or re-run the wrong step. For the automatic ResumeForward path, degrade to a full restart
        // (safer than a mis-aligned skip); for an explicit operator override, honor the intent but warn.
        // This detects add/remove, not reorder; reordering steps across a restart is an accepted limitation.
        var resolvedPolicy = policy;
        if (run.StepCount != CountDefinitionSteps(def))
        {
            if (policy == ResumePolicy.ResumeForward)
            {
                _logger.LogWarning(
                    "Batch run {BatchId}: definition {DefId} step count changed since the run started ({Old} -> {New}); resuming with RestartAll instead of ResumeForward to avoid a mis-aligned skip.",
                    batchId, def.Id, run.StepCount, CountDefinitionSteps(def));
                resolvedPolicy = ResumePolicy.RestartAll;
            }
            else
            {
                _logger.LogWarning(
                    "Batch run {BatchId}: definition {DefId} step count changed since the run started ({Old} -> {New}); honoring the explicit resume policy regardless.",
                    batchId, def.Id, run.StepCount, CountDefinitionSteps(def));
            }
        }

        var orderedTopLevelCount = def.Steps.Count;
        var startStepIndex = Math.Clamp(resolvedPolicy.ResolveStartIndex(run.CurrentStepIndex), 0, orderedTopLevelCount);

        // Cursor already at/after the end → the run actually finished every step (the crash happened after
        // the last step but before completion was recorded); just finalize the record without dispatching.
        if (startStepIndex >= orderedTopLevelCount)
        {
            await CompleteRunRecordAsync(batchId, runtimeTerminal: null).ConfigureAwait(false);
            return;
        }

        var executor = new BatchExecutor(
            this,
            _serviceProvider.GetRequiredService<IApprovalGateCoordinator>(),
            _serviceProvider.GetRequiredService<IJobExecutionAwaiter>(),
            _transport,
            ResolveThisServiceName(),
            _clock,
            _serviceProvider.GetRequiredService<ILogger<BatchExecutor>>(),
            // Forwarded state first, cursor second (see TriggerBatchAsync for the crash-window rationale):
            // a crash between the two writes re-runs the just-completed step rather than dropping its output.
            onStepCompleted: async (nextIndex, forwardedState, ct) =>
            {
                await _batchRunStore.UpdateForwardedStateAsync(batchId, forwardedState, ct).ConfigureAwait(false);
                await _batchRunStore.UpdateCursorAsync(batchId, nextIndex, ct).ConfigureAwait(false);
            },
            // Bind the resume idempotency probes (null on the trigger path). The gate probe honors a gate
            // already decided before the crash; the shadow probe skips a cross-service step that already
            // terminated. Without these, resume re-opens approved gates and repeats completed remote work.
            resumeGateProbe: _serviceProvider.GetRequiredService<IResumeGateProbe>(),
            resumeShadowProbe: _serviceProvider.GetRequiredService<IResumeShadowProbe>());

        // Per-run cancellation, identical to TriggerBatchAsync: link the host-stopping token so an
        // administrative cancel trips only this run; the host token still cancels every run on shutdown.
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_hostStopping);
        _batchRunRegistry.Register(batchId, runCts);
        var runToken = runCts.Token;

        _ = Task.Run(async () =>
        {
            JobStatus? runtimeTerminal = null;
            // Same host-shutdown discrimination as TriggerBatchAsync: a second deploy during a long
            // approval window must leave the resumed run in-flight (not Cancelled), so it resumes again
            // on the next start. This makes resume re-entrant across consecutive restarts.
            var hostShuttingDown = false;
            try
            {
                await executor.RunAsync(
                    def, batchId, ResumeParameters(run), triggeredBy: run.TriggeredBy, runToken, startStepIndex,
                    ResumeForwardedOutputs(run)).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // Parent host-stopping token cancelled → graceful shutdown → leave in-flight; otherwise an
                // administrative cancel → record Cancelled. See TriggerBatchAsync for the full rationale.
                if (_hostStopping.IsCancellationRequested)
                {
                    hostShuttingDown = true;
                    _logger.LogInformation(ex,
                        "Resumed batch {BatchId} (definition {DefId}) interrupted by host shutdown; left in-flight for durable resume.",
                        batchId, def.Id);
                }
                else
                {
                    runtimeTerminal = JobStatus.Cancelled;
                    _logger.LogWarning(ex, "Resumed batch {BatchId} (definition {DefId}) cancelled.", batchId, def.Id);
                }
            }
            catch (Exception ex)
            {
                runtimeTerminal = JobStatus.Failed;
                _logger.LogError(ex, "Resumed batch {BatchId} (definition {DefId}) failed.", batchId, def.Id);
            }
            finally
            {
                // Skip the terminal finalize ONLY on host shutdown — the run stays in-flight for the next
                // host's recovery. The CTS/registry teardown and the completion signal still run in all cases.
                if (!hostShuttingDown)
                {
                    await CompleteRunRecordAsync(batchId, runtimeTerminal).ConfigureAwait(false);
                }
                _batchRunRegistry.Remove(batchId);
                runCts.Dispose();
                _batchCompletionSignal.Signal(new BatchCompletionSignalPayload
                {
                    BatchRunId = batchId,
                    BatchDefinitionId = def.Id,
                    BatchName = def.Name,
                    RuntimeTerminalStatus = runtimeTerminal,
                });
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Initial parameters supplied to a resumed run, rehydrated from the persisted forwarded state. Falls
    /// back to <see cref="JobParameters.Empty"/> for a run created before forwarded state existed or on a
    /// store that does not persist it.
    /// </summary>
    private static JobParameters ResumeParameters(BatchRun run)
    {
        if (run.ForwardedState is { Count: > 0 } state
            && state.TryGetValue(ForwardedStateKeys.InitialParameters, out var raw)
            && AsDict(raw) is { } initial)
        {
            return new JobParameters(initial);
        }
        return JobParameters.Empty;
    }

    /// <summary>
    /// Accumulated step outputs to seed the forwarding accumulator on resume, rehydrated from the persisted
    /// forwarded state; <c>null</c> when none was recorded.
    /// </summary>
    private static Dictionary<string, object?>? ResumeForwardedOutputs(BatchRun run)
    {
        if (run.ForwardedState is { Count: > 0 } state
            && state.TryGetValue(ForwardedStateKeys.ForwardedOutputs, out var raw))
        {
            return AsDict(raw);
        }
        return null;
    }

    /// <summary>
    /// Coerces a forwarded-state value into a dictionary. It is a live dictionary in-process, but a
    /// <see cref="JsonElement"/> after a round-trip through a JSON-backed store; both are handled.
    /// </summary>
    private static Dictionary<string, object?>? AsDict(object? raw)
        => raw switch
        {
            IReadOnlyDictionary<string, object?> dict => new Dictionary<string, object?>(dict, StringComparer.Ordinal),
            JsonElement { ValueKind: JsonValueKind.Object } element => element.Deserialize<Dictionary<string, object?>>(),
            _ => null,
        };

    // ===== IJobRunnerInternal =====

    /// <inheritdoc/>
    public async Task<JobExecution> TriggerInternalAsync(
        string jobName,
        JobParameters parameters,
        string? triggeredBy,
        string? batchId,
        string? stepId,
        string? predefinedExecutionId,
        string? batchDefinitionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        ArgumentNullException.ThrowIfNull(parameters);

        var def = _jobRegistry.TryGet(jobName)
            ?? throw new JobNotRegisteredException($"Job '{jobName}' is not registered.") { JobName = jobName };

        var executionId = predefinedExecutionId ?? IdGenerator.NewExecutionId();
        var now = _clock.GetUtcNow();
        var execution = new JobExecution
        {
            ExecutionId = executionId,
            JobName = def.Name,
            BatchId = batchId,
            BatchStepId = stepId,
            BatchDefinitionId = batchDefinitionId,
            Status = JobStatus.Pending,
            Parameters = parameters.Values,
            EnqueuedAtUtc = now,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            AttemptNumber = 1,
            MaxRetries = def.MaxRetries,
            LastError = null,
            Processed = 0,
            Failed = 0,
            Total = null,
            TriggeredBy = triggeredBy,
            WorkerName = null,
        };

        // Insert with the predefined id if the store implements IJobStoreInternal; otherwise
        // fall back to CreateAsync which generates its own id. The fallback path silently drops
        // JobExecution.BatchDefinitionId since CreateAsync(JobDefinition) constructs the row without
        // it. Emit a diagnostic warning so adapter authors discover the gap at adapter test time
        // rather than at silent data-correctness regression time. EF/Redis adapters MUST implement
        // InsertAsync(JobExecution, CT) AND mirror the fallback-path contract test in their package.
        if (_jobStore is IJobStoreInternal storeInternal)
        {
            execution = await storeInternal.InsertAsync(execution, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Emit the warning ONCE per adapter type per process to avoid alert flooding in
            // production when an EF/Redis adapter author forgets the InsertAsync overload. The
            // diagnostic value (adapter author discovers the gap at adapter test time) is preserved
            // by the first-call emission.
            // Key on FullName (namespace-qualified) so two adapters sharing a simple type name in
            // different namespaces do not suppress each other's one-shot warning.
            var adapterTypeName = _jobStore.GetType().FullName ?? _jobStore.GetType().Name;
            bool shouldWarn;
            lock (_warnLock)
            {
                shouldWarn = _warnedAdapterTypes.Add(adapterTypeName);
            }
            if (shouldWarn)
            {
                _logger.LogWarning(
                    "JobStore adapter {Type} does not implement InsertAsync(JobExecution) — falling back to CreateAsync(JobDefinition). BatchDefinitionId will be lost. Persistent store adapters MUST implement InsertAsync. (This warning emits once per adapter type per process.)",
                    adapterTypeName);
            }
            execution = await _jobStore.CreateAsync(def, cancellationToken).ConfigureAwait(false);
        }

        var request = new JobExecutionRequest
        {
            ExecutionId = execution.ExecutionId,
            Definition = def,
            Parameters = parameters,
            AttemptNumber = 1,
            TriggeredBy = triggeredBy,
            BatchId = batchId,
            BatchStepId = stepId,
            EnqueuedAtUtc = now,
        };
        await _dispatcher.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        return execution;
    }

    /// <inheritdoc/>
    public async Task<string> RecordCrossServiceStartAsync(JobExecution running, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(running);
        // Store-guard mirror of the local path in TriggerInternalAsync. Only IJobStoreInternal can
        // carry BatchId/BatchStepId/WorkerName at insert time; a non-internal store silently disables
        // cross-service tracking (no CreateAsync fallback — it cannot carry those fields).
        if (_jobStore is IJobStoreInternal storeInternal)
        {
            await storeInternal.InsertAsync(running, cancellationToken).ConfigureAwait(false);
        }
        return running.ExecutionId;
    }

    /// <inheritdoc/>
    public async Task RecordCrossServiceEndAsync(string executionId, JobResult result, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        ArgumentNullException.ThrowIfNull(result);
        // Start was skipped if the store is not IJobStoreInternal → nothing to update (symmetric no-op,
        // avoids a KeyNotFoundException from a bare UpdateStatusAsync). Running -> terminal is LEGAL.
        if (_jobStore is IJobStoreInternal)
        {
            // The shadow row is in Running and never passed through Cancelling, so Running -> Cancelled
            // is ILLEGAL (per JobStatusTransitions). A worker that finalised its OWN row as Cancelled
            // (host shutdown / pre-dispatch cancel) returns Status=Cancelled here verbatim (the HTTP
            // and RabbitMQ receivers copy the worker status). Collapse it to Failed — the only legal
            // Running terminal for a non-success — mirroring the OCE cancel arms in the executors.
            var terminalStatus = result.Status == JobStatus.Cancelled ? JobStatus.Failed : result.Status;
            var error = result.Status == JobStatus.Cancelled
                ? (result.ErrorMessage ?? "cross-service step cancelled by remote worker")
                : result.ErrorMessage;
            // CT-decouple (CancellationToken.None) per the cancel-path precedent: the terminal write
            // MUST land even if the batch CT just tripped, else the row orphans in Running.
            try
            {
                await _jobStore.UpdateStatusAsync(
                    executionId, terminalStatus, error, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Host shutting down — EF's pooled context factory may throw at disposal.
                // Finalization is best-effort; don't surface a scary dispose trace.
            }
        }
    }
}
