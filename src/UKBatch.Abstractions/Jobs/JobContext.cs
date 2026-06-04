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
}
