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
        var executor = new BatchExecutor(
            this,
            _serviceProvider.GetRequiredService<IApprovalGateCoordinator>(),
            _serviceProvider.GetRequiredService<IJobExecutionAwaiter>(),
            _transport,
            ResolveThisServiceName(),
            _clock,
            _serviceProvider.GetRequiredService<ILogger<BatchExecutor>>());

        // Fire-and-forget against the HOST's lifetime, NOT the caller's CT.
        var hostStopping = _hostStopping;
        _ = Task.Run(async () =>
        {
            // Capture the runtime's terminal verdict so the hub fan-out can override the row-derived
            // aggregate. A failing approval gate (rejected / dismissed / timed-out-Fail) rethrows out
            // of RunAsync but leaves NO JobExecution row, so without this the row aggregate would
            // report the run Completed (green) even though the batch ended in failure.
            JobStatus? runtimeTerminal = null;
            try
            {
                await executor.RunAsync(def, batchId, initialParameters ?? JobParameters.Empty, triggeredBy, hostStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // Host stop / batch cancellation — a clean teardown, not a failure. Must precede the
                // general Exception catch (subtype) and logs at Warning, not Error.
                runtimeTerminal = JobStatus.Cancelled;
                _logger.LogWarning(ex, "Batch {BatchId} (definition {DefId}) cancelled.", batchId, batchDefinitionId);
            }
            catch (Exception ex)
            {
                runtimeTerminal = JobStatus.Failed;
                _logger.LogError(ex, "Batch {BatchId} (definition {DefId}) failed.", batchId, batchDefinitionId);
            }
            finally
            {
                // Signal the hub fan-out that the batch run has finished. The payload carries
                // (BatchRunId, BatchDefinitionId, BatchName) so the hub fan-out can populate
                // BatchCompletionSummary.BatchDefinitionId without an IBatchCatalogService roundtrip.
                // RuntimeTerminalStatus carries the closure's verdict (null on clean completion).
                // The hub queries the store ONCE per signal to compute the aggregate shard counts.
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
