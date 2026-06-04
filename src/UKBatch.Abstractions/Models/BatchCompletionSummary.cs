namespace UKBatch.Abstractions.Models;

/// <summary>
/// Summary of a terminated batch run, pushed once via the SignalR hub
/// (<c>IJobStatusHubClient.BatchCompleted</c>) when the LAST job execution in the batch
/// reaches a terminal state. Public Abstractions type so adapter packages and the Blazor
/// dashboard can deserialize it without referencing the Api package.
/// </summary>
/// <remarks>
/// <see cref="FinalStatus"/> is the aggregate result derived by the fan-out pump:
/// <list type="bullet">
///   <item><see cref="JobStatus.Completed"/> when every child execution Completed.</item>
///   <item><see cref="JobStatus.Failed"/> when at least one execution Failed and none Cancelled.</item>
///   <item><see cref="JobStatus.Cancelled"/> when at least one execution Cancelled.</item>
/// </list>
/// </remarks>
public sealed record class BatchCompletionSummary
{
    /// <summary>Batch run id (UUIDv7 returned from <c>IJobRunner.TriggerBatchAsync</c>).</summary>
    public required string BatchId { get; init; }

    /// <summary>Definition id this run was instantiated from.</summary>
    public required string BatchDefinitionId { get; init; }

    /// <summary>Display name of the definition (resolved via <c>IBatchCatalogService</c> at completion time).</summary>
    public required string BatchName { get; init; }

    /// <summary>Aggregate terminal status — Completed / Failed / Cancelled.</summary>
    public required JobStatus FinalStatus { get; init; }

    /// <summary>Total number of child executions in the batch run.</summary>
    public required int TotalJobs { get; init; }

    /// <summary>Number of executions that ended in <see cref="JobStatus.Completed"/>.</summary>
    public required int SucceededJobs { get; init; }

    /// <summary>Number of executions that ended in <see cref="JobStatus.Failed"/>.</summary>
    public required int FailedJobs { get; init; }

    /// <summary>Number of executions that ended in <see cref="JobStatus.Cancelled"/>.</summary>
    public required int CancelledJobs { get; init; }

    /// <summary>UTC instant the LAST execution terminated.</summary>
    public required DateTimeOffset CompletedAtUtc { get; init; }
}
