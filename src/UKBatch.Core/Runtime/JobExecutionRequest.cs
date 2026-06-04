using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Internal envelope routed through the <see cref="JobDispatcher"/> to a <c>JobWorker</c>.
/// Pre-resolved execution id + definition + parameters keep the worker's per-request critical
/// section free of dictionary lookups against the registry.
/// </summary>
internal sealed record class JobExecutionRequest
{
    /// <summary>Pre-allocated execution id (required for awaiter ordering).</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Resolved job definition.</summary>
    public required JobDefinition Definition { get; init; }

    /// <summary>Effective parameters for this attempt.</summary>
    public required JobParameters Parameters { get; init; }

    /// <summary>1-based attempt counter.</summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Identity that triggered the execution (user, "scheduler", "api", worker name); <c>null</c> if unknown.</summary>
    public required string? TriggeredBy { get; init; }

    /// <summary>Parent batch id, if this execution is a batch step.</summary>
    public required string? BatchId { get; init; }

    /// <summary>Parent batch step id, if this execution is a batch step.</summary>
    public required string? BatchStepId { get; init; }

    /// <summary>UTC time this request was enqueued.</summary>
    public required DateTimeOffset EnqueuedAtUtc { get; init; }
}
