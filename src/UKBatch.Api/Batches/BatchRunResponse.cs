namespace UKBatch.Api.Batches;

/// <summary>Body for <c>202 Accepted</c> from <c>POST /batches/[by-id|by-name]/{...}/run</c>.</summary>
public sealed record class BatchRunResponse
{
    /// <summary>Allocated batch run id (UUIDv7); track via <c>GET /batches/{batchRunId}/status</c>.</summary>
    public required string BatchId { get; init; }
}
