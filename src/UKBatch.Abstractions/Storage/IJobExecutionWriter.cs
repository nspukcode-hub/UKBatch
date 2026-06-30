using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Write-side of the execution-history store. Consumed by the runtime dispatcher, worker, and
/// retry orchestrator. Implementations MUST be thread-safe and durable across host restarts when
/// their backing store is durable.
/// </summary>
public interface IJobExecutionWriter
{
    /// <summary>Creates a new execution row in <see cref="JobStatus.Pending"/> state and returns the persisted entity.</summary>
    Task<JobExecution> CreateAsync(JobDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically updates status. Stores MUST reject illegal transitions
    /// (see <see cref="JobStatus"/> state machine) by throwing <see cref="InvalidOperationException"/>.
    /// </summary>
    Task UpdateStatusAsync(string executionId, JobStatus status, string? errorMessage, CancellationToken cancellationToken);

    /// <summary>Persists an explicit attempt counter bump; called by the retry orchestrator before re-dispatch.</summary>
    Task RecordAttemptAsync(string executionId, int attemptNumber, CancellationToken cancellationToken);

    /// <summary>Persists a progress snapshot from the runtime's <see cref="Jobs.IJobProgress"/>.</summary>
    Task UpdateProgressAsync(string executionId, long processed, long failed, long? total, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the output values a job recorded (<see cref="Models.JobExecution.Outputs"/>). Called on
    /// the success path before the terminal status flip, only when the job produced output. The default
    /// no-op lets a store that does not persist outputs degrade to "no forwarding" (the pre-feature
    /// behavior), mirroring <see cref="IBatchRunStore.UpdateForwardedStateAsync"/>; the shipped
    /// InMemory / EF stores override it (and throw if the execution id is absent, like the other writers).
    /// </summary>
    Task UpdateOutputsAsync(string executionId, IReadOnlyDictionary<string, object?> outputs, CancellationToken cancellationToken)
        => Task.CompletedTask;   // default no-op: forward-compat for external stores
}
