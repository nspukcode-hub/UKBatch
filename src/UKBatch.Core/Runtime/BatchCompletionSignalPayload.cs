namespace UKBatch.Runtime;

/// <summary>
/// Payload written to <see cref="IBatchCompletionEvents.CompletedBatchRunIds"/> when a batch run
/// finishes. Carries the run id + definition id + display name so the hub fan-out can construct
/// the public <see cref="UKBatch.Abstractions.Models.BatchCompletionSummary"/> WITHOUT a roundtrip
/// to <c>IBatchCatalogService</c> for name resolution.
/// </summary>
/// <remarks>
/// <para>Internal — friend-accessible to <c>UKBatch.Api</c> via <c>InternalsVisibleTo</c>.</para>
/// <para><b>Why not <c>BatchCompletionSummary</c> directly:</b> the public type aggregates SHARD
/// COUNTS (TotalJobs, SucceededJobs, FailedJobs, CancelledJobs) that the runtime cannot compute
/// without querying the store. The signal payload is the runtime's "I'm done" notice; the hub
/// fan-out queries the store once per signal to compute the public summary. Cleaner separation of
/// concerns: runtime knows when, store knows what, hub builds the wire shape.</para>
/// </remarks>
internal sealed record class BatchCompletionSignalPayload
{
    /// <summary>The batch RUN id (UUIDv7 returned from <c>TriggerBatchAsync</c>).</summary>
    public required string BatchRunId { get; init; }

    /// <summary>The batch DEFINITION id that this run was instantiated from.</summary>
    public required string BatchDefinitionId { get; init; }

    /// <summary>Display name of the definition at trigger time (resolved from <c>BatchDefinition.Name</c>).</summary>
    public required string BatchName { get; init; }
}
