using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Internal Core seam exposing the runtime entry point with full context
/// (batch id, step id, optional pre-allocated execution id). Public callers use
/// <see cref="IJobRunner"/>; both interfaces resolve to the same singleton.
/// </summary>
internal interface IJobRunnerInternal
{
    /// <summary>
    /// Triggers a job execution with full runtime context.
    /// </summary>
    /// <param name="jobName">Logical job name (must be registered).</param>
    /// <param name="parameters">Effective parameters for this trigger.</param>
    /// <param name="triggeredBy">Identity that triggered the execution; null if unknown.</param>
    /// <param name="batchId">Parent batch id; null for standalone executions.</param>
    /// <param name="stepId">Parent batch step id; null for standalone executions.</param>
    /// <param name="predefinedExecutionId">
    /// When non-null, the runner uses this id verbatim (caller must have obtained it from
    /// <see cref="Internal.IdGenerator.NewExecutionId"/>). Required by the awaiter-before-trigger
    /// ordering invariant. When null, the runner generates a fresh id.
    /// </param>
    /// <param name="batchDefinitionId">
    /// Parent batch DEFINITION id; non-null only for batch-spawned executions.
    /// Propagated to <see cref="JobExecution.BatchDefinitionId"/> so the dashboard can
    /// query "last N runs of this definition" via <c>QueryAsync({ BatchDefinitionId = ... })</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<JobExecution> TriggerInternalAsync(
        string jobName,
        JobParameters parameters,
        string? triggeredBy,
        string? batchId,
        string? stepId,
        string? predefinedExecutionId,
        string? batchDefinitionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the SERVER-SIDE start of a cross-service batch step before the
    /// transport request is dispatched, so the dashboard's batch-run-detail, completion counts,
    /// DAG node coloring, and run history light up for cross-service steps (which execute on a
    /// remote worker and would otherwise leave the orchestrator's <c>IJobStore</c> empty).
    /// The runner inserts the supplied <see cref="JobExecution"/> in state <see cref="JobStatus.Running"/>
    /// (or no-ops if the configured store is not an <see cref="Abstractions.Storage.IJobStoreInternal"/>)
    /// and returns the execution id the caller MUST pass to <see cref="RecordCrossServiceEndAsync"/>.
    /// </summary>
    /// <param name="running">A fully-formed execution row in state <see cref="JobStatus.Running"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<string> RecordCrossServiceStartAsync(JobExecution running, CancellationToken cancellationToken);

    /// <summary>
    /// Transitions the row started by <see cref="RecordCrossServiceStartAsync"/> to its
    /// terminal state from the worker's <see cref="JobResult"/>. No-op if the configured store is not an
    /// <see cref="Abstractions.Storage.IJobStoreInternal"/> (the start was skipped symmetrically).
    /// </summary>
    /// <param name="executionId">The id returned by <see cref="RecordCrossServiceStartAsync"/>.</param>
    /// <param name="result">The worker's terminal result.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RecordCrossServiceEndAsync(string executionId, JobResult result, CancellationToken cancellationToken);
}
