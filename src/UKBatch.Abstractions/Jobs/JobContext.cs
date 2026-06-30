using Microsoft.Extensions.Logging;

namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Runtime context handed to a job execution. Lifetime is one execution; it is NOT safe to retain
/// or share across executions. Tests construct instances directly via the <see langword="required"/>
/// init properties; runtime callers receive a fully-populated instance from the dispatcher.
/// </summary>
public sealed class JobContext
{
    /// <summary>Unique identifier of this execution.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Identifier of the batch this execution belongs to, or <c>null</c> if executed standalone.</summary>
    public string? BatchId { get; init; }

    /// <summary>Identifier of the parent batch step that scheduled this execution, or <c>null</c>.</summary>
    public string? BatchStepId { get; init; }

    /// <summary>Logical job name (matches <see cref="Models.JobDefinition.Name"/>).</summary>
    public required string JobName { get; init; }

    /// <summary>Typed accessor over the parameter dictionary.</summary>
    public required JobParameters Parameters { get; init; }

    /// <summary>
    /// Per-execution DI scope service provider. Resolving from the root provider is forbidden;
    /// scoped services resolved here are disposed when the execution completes.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Logger pre-scoped with the job name and execution id.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Progress reporter; fires runtime events consumed by dashboards.</summary>
    public required IJobProgress Progress { get; init; }

    /// <summary>
    /// Parallel-for-each executor for inline data parallelism inside an <see cref="IJob"/>
    /// (consumed via <see cref="JobContextParallelExtensions.ParallelForEachAsync{TItem}"/>).
    /// Implementations MUST be thread-safe.
    /// </summary>
    public required IParallelExecutor ParallelExecutor { get; init; }

    /// <summary>
    /// 1-based attempt counter for THIS execution; <c>1</c> on first run, <c>2</c> after the first retry, etc.
    /// <para>Snapshot value for this execution only; the authoritative value across retries lives in
    /// <see cref="Models.JobExecution.AttemptNumber"/> via <see cref="Storage.IJobExecutionReader.GetAsync"/>.</para>
    /// </summary>
    public required int AttemptNumber { get; init; }

    /// <summary>UTC time at which this execution started.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Identity that triggered this execution (user, <c>"scheduler"</c>, <c>"api"</c>, or worker name); <c>null</c> if unknown.</summary>
    public string? TriggeredBy { get; init; }

    /// <summary>
    /// Output sink for this execution. Values written via <see cref="JobOutputs.Set"/> are forwarded
    /// into the parameters of later steps in the same batch, and returned to the orchestrator for a
    /// cross-service step. Empty by default; a job that writes nothing changes no behavior. Thread-safe,
    /// so it is safe to write from partition workers.
    /// </summary>
    public JobOutputs Outputs { get; init; } = new();

    // AsyncLocal owns the per-worker index. Encapsulated: there is no public mutable property
    // because a settable property would race across the N concurrent partition workers that share
    // this single JobContext instance. The default value 0 covers plain (non-partitioned) jobs and
    // any read taken outside a worker scope.
    private static readonly AsyncLocal<int> _workerIndex = new();

    /// <summary>
    /// 0-based index of the partition worker executing the current call, for an
    /// <see cref="IPartitionedJob{TItem}"/> or an inline <c>ParallelForEachAsync</c> body.
    /// Stable for the lifetime of a worker; distinct concurrent workers observe distinct values
    /// in <c>[0, workerCount)</c>. A plain <see cref="IJob"/> (no fan-out) always reads <c>0</c>.
    /// <para>Use it to shard side state (e.g. a per-worker buffer, a connection from a sized pool).
    /// Do NOT use it as a stable identity across runs — it is a per-run worker slot, not a worker id.</para>
    /// </summary>
    // Intentionally an instance member: jobs read it ergonomically as ctx.WorkerIndex alongside the
    // other per-execution context. The backing AsyncLocal is static by necessity, but the API must
    // stay on the instance, so the "could be static" suggestion does not apply.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Public ergonomic context accessor; must remain an instance member.")]
    public int WorkerIndex => _workerIndex.Value;

    /// <summary>
    /// Establishes the <see cref="WorkerIndex"/> for the current async flow and everything it
    /// awaits, until the returned scope is disposed. Intended for the runtime's fan-out only;
    /// the value flows via <see cref="AsyncLocal{T}"/> to every job body invoked on the worker.
    /// </summary>
    /// <param name="workerIndex">0-based worker slot; must be &gt;= 0.</param>
    public static IDisposable EnterWorkerScope(int workerIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerIndex);
        var previous = _workerIndex.Value;
        _workerIndex.Value = workerIndex;
        return new WorkerScope(previous);
    }

    private sealed class WorkerScope(int previous) : IDisposable
    {
        private readonly int _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _workerIndex.Value = _previous;
        }
    }
}
