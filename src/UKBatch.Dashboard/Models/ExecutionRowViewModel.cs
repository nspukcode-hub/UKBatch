using UKBatch.Abstractions.Models;

namespace UKBatch.Dashboard.Models;

/// <summary>View model adapter from <see cref="JobExecution"/> for execution list rows.</summary>
public sealed record class ExecutionRowViewModel
{
    /// <summary>Execution id.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Logical job name.</summary>
    public required string JobName { get; init; }

    /// <summary>Parent batch run id, or <c>null</c> for standalone executions.</summary>
    public string? BatchId { get; init; }

    /// <summary>
    /// Identifier of the batch STEP that scheduled this execution, or <c>null</c> for standalone jobs.
    /// Live DAG join key (== <see cref="JobExecution.BatchStepId"/>) — <c>Batches/RunDetail</c> projects
    /// rows into a <c>StepId → JobStatus</c> map for the live <c>DagView</c>.
    /// </summary>
    public string? BatchStepId { get; init; }

    /// <summary>Parent batch definition id, or <c>null</c> for standalone executions.</summary>
    public string? BatchDefinitionId { get; init; }

    /// <summary>Current execution status.</summary>
    public required JobStatus Status { get; init; }

    /// <summary>UTC enqueue time.</summary>
    public required DateTimeOffset EnqueuedAtUtc { get; init; }

    /// <summary>UTC time the runtime started; <c>null</c> before Running.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>UTC terminal-state time; <c>null</c> if not terminal.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>1-based attempt counter.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Effective retry budget.</summary>
    public required int MaxRetries { get; init; }

    /// <summary>Items processed (partitioned jobs).</summary>
    public required long Processed { get; init; }

    /// <summary>Items failed (partitioned jobs).</summary>
    public required long Failed { get; init; }

    /// <summary>Total expected items; <c>null</c> when unknown.</summary>
    public long? Total { get; init; }

    /// <summary>Most recent error message, or <c>null</c>.</summary>
    public string? LastError { get; init; }

    /// <summary>Maps from an Abstractions <see cref="JobExecution"/> to the view model.</summary>
    public static ExecutionRowViewModel FromExecution(JobExecution exec)
    {
        ArgumentNullException.ThrowIfNull(exec);
        return new ExecutionRowViewModel
        {
            ExecutionId = exec.ExecutionId,
            JobName = exec.JobName,
            BatchId = exec.BatchId,
            BatchStepId = exec.BatchStepId,
            BatchDefinitionId = exec.BatchDefinitionId,
            Status = exec.Status,
            EnqueuedAtUtc = exec.EnqueuedAtUtc,
            StartedAtUtc = exec.StartedAtUtc,
            CompletedAtUtc = exec.CompletedAtUtc,
            AttemptNumber = exec.AttemptNumber,
            MaxRetries = exec.MaxRetries,
            Processed = exec.Processed,
            Failed = exec.Failed,
            Total = exec.Total,
            LastError = exec.LastError,
        };
    }
}
