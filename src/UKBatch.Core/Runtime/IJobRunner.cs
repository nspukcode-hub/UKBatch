using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Public entry point for manual / API-triggered job execution. Implemented by the runtime;
/// consumers resolve <see cref="IJobRunner"/> from DI. <c>BatchExecutor</c> consumes the internal
/// <see cref="IJobRunnerInternal"/> seam instead.
/// </summary>
public interface IJobRunner
{
    /// <summary>Triggers a single job execution.</summary>
    Task<JobExecution> TriggerAsync(
        string jobName,
        JobParameters parameters,
        string? triggeredBy,
        CancellationToken cancellationToken);

    /// <summary>Triggers a batch by definition id; returns the batch id (one per run).</summary>
    /// <remarks>Validates the definition synchronously and throws
    /// <see cref="BatchTriggerValidationException"/> (structural errors or an unregistered job)
    /// before the fire-and-forget run begins — a non-HTTP caller must be prepared for the throw.</remarks>
    Task<string> TriggerBatchAsync(
        string batchDefinitionId,
        JobParameters? initialParameters,
        string? triggeredBy,
        CancellationToken cancellationToken);

    /// <summary>Cancels an in-flight execution.</summary>
    Task CancelAsync(string executionId, CancellationToken cancellationToken);
}
