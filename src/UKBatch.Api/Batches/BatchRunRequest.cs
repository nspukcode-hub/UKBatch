namespace UKBatch.Api.Batches;

/// <summary>Body for <c>POST /batches/by-id/{id}/run</c> + <c>POST /batches/by-name/{name}/run</c>.</summary>
public sealed record class BatchRunRequest
{
    /// <summary>Initial parameters forwarded to every job execution in the batch.</summary>
    public IReadOnlyDictionary<string, object?>? InitialParameters { get; init; }

    /// <summary>
    /// Optional identity to attribute the trigger to. Falls back to
    /// <c>IJobTriggerContext.GetTriggeredByOrNull()</c> when absent.
    /// </summary>
    public string? TriggeredBy { get; init; }
}
