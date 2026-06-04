namespace UKBatch.Api.Jobs;

/// <summary>Body for <c>202 Accepted</c> from <c>POST /jobs/{name}/trigger</c>.</summary>
public sealed record class JobTriggerResponse
{
    /// <summary>Allocated execution id; track via <c>GET /executions/{id}</c>.</summary>
    public required string ExecutionId { get; init; }
}
