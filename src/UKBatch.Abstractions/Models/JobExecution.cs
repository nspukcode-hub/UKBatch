namespace UKBatch.Abstractions.Models;

/// <summary>Immutable snapshot of a job execution as persisted by <see cref="Storage.IJobStore"/>.</summary>
public sealed record class JobExecution
{
    /// <summary>Unique execution id.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Logical job name.</summary>
    public required string JobName { get; init; }

    /// <summary>Identifier of the parent batch run; <c>null</c> for standalone jobs.</summary>
    public string? BatchId { get; init; }

    /// <summary>Identifier of the batch step that scheduled this; <c>null</c> for standalone jobs.</summary>
    public string? BatchStepId { get; init; }

    /// <summary>
    /// Identifier of the batch DEFINITION (NOT the batch run) that scheduled this execution;
    /// <c>null</c> for standalone jobs OR for batch executions persisted before this field existed.
    /// The runtime fills this in during <c>JobRunner.TriggerBatchAsync</c> for every batch-spawned
    /// execution; the dashboard navigates "last N runs of this definition" via
    /// <c>IJobExecutionReader.QueryAsync({ BatchDefinitionId = ... })</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Storage adapter contract:</b> EF Core / Redis / RabbitMQ implementations
    /// MUST round-trip this field. The <c>InMemoryJobStore.InsertAsync</c> path is the only adapter
    /// supplying it today; adapter packages MUST land this field at <c>INSERT</c> time before going
    /// live.</para>
    /// <para><b>Adapter forward-compat guard:</b> a fallback path on a non-InMemory
    /// store would silently drop this field. The runtime logs a diagnostic warning via
    /// <c>ILogger.LogWarning</c> when <c>JobRunner.TriggerInternalAsync</c> falls back to
    /// <c>IJobStore.CreateAsync(JobDefinition)</c> instead of <c>InMemoryJobStore.InsertAsync</c>.
    /// EF/Redis adapter authors MUST implement <c>InsertAsync(JobExecution, CT)</c> AND
    /// mirror this contract in their adapter test suite.</para>
    /// <para><b>Asymmetry with <see cref="BatchId"/>:</b> <see cref="BatchId"/> is the RUN id
    /// (UUIDv7, one per <c>TriggerBatchAsync</c> invocation); <see cref="BatchDefinitionId"/> is
    /// the DEFINITION id (stable per <c>AddBatch</c> or per Store row). One definition has N runs;
    /// one run has M executions.</para>
    /// </remarks>
    public string? BatchDefinitionId { get; init; }

    /// <summary>Current state in the lifecycle (see <see cref="JobStatus"/> for state machine).</summary>
    public required JobStatus Status { get; init; }

    /// <summary>Static parameters at dispatch time. Values are JSON-serializable.</summary>
    public required IReadOnlyDictionary<string, object?> Parameters { get; init; }

    /// <summary>UTC enqueue time.</summary>
    public required DateTimeOffset EnqueuedAtUtc { get; init; }

    /// <summary>UTC time the runtime started executing; <c>null</c> before <see cref="JobStatus.Running"/>.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>UTC time the execution reached a terminal state.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>1-based attempt counter; equal to <c>1</c> on first run, bumped by the retry orchestrator.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>
    /// Effective retry budget for this execution after attribute / fluent / runtime inheritance is resolved.
    /// Excludes the initial attempt; e.g. <c>3</c> means up to 4 total tries.
    /// </summary>
    public required int MaxRetries { get; init; }

    /// <summary>Error message on the most recent failed attempt, or <c>null</c>.</summary>
    public string? LastError { get; init; }

    /// <summary>Items successfully processed (partitioned jobs); <c>0</c> for non-partitioned.</summary>
    public required long Processed { get; init; }

    /// <summary>Items permanently failed (partitioned jobs); <c>0</c> for non-partitioned.</summary>
    public required long Failed { get; init; }

    /// <summary>Total expected items; <c>null</c> when unknown.</summary>
    public long? Total { get; init; }

    /// <summary>Identity that triggered the execution.</summary>
    public string? TriggeredBy { get; init; }

    /// <summary>Worker name that picked up the job (worker mode); <c>null</c> for in-process execution.</summary>
    public string? WorkerName { get; init; }
}
