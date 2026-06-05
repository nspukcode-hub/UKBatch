namespace UKBatch.Runtime;

/// <summary>
/// Thrown internally when a job execution exceeds its configured per-execution timeout
/// (<c>JobDefinition.TimeoutSeconds</c>). Routed through the normal retry decision — a timeout is a
/// retry-eligible failure, distinct from a deliberate cancellation (which is terminal and never retried).
/// </summary>
internal sealed class JobExecutionTimeoutException : Exception
{
    public JobExecutionTimeoutException(string message) : base(message) { }
}
