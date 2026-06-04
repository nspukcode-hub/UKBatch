namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>JobRunner.CancelAsync</c> (and consumed by REST endpoints for the
/// 404 mapping on <c>POST /executions/{id}/cancel</c> + <c>GET /executions/{id}</c>) when the
/// referenced execution id does not exist in the job store.
/// </summary>
/// <remarks>
/// Inherits <see cref="InvalidOperationException"/> so existing test setups that assert on the
/// base type continue to pass without change — a zero-test-churn promise.
/// </remarks>
public sealed class JobExecutionNotFoundException : InvalidOperationException
{
    /// <summary>The missing execution id; <c>null</c> if the caller did not supply context.</summary>
    public string? ExecutionId { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public JobExecutionNotFoundException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public JobExecutionNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
