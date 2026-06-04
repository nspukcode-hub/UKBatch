namespace UKBatch.Runtime;

/// <summary>
/// Strategy for deciding whether and how long to wait before retrying a failed job execution.
/// Hand-rolled in Core (no Polly dependency at the orchestrator level — Polly is reserved for
/// per-item <c>RetryThenContinue</c> on partitioned jobs).
/// </summary>
internal interface IRetryPolicy
{
    /// <summary>
    /// Inspects the just-failed attempt and returns the decision.
    /// </summary>
    /// <param name="attemptNumber">1-based number of the attempt that just failed.</param>
    /// <param name="maxRetries">Configured max retries (excluding the initial attempt).</param>
    /// <param name="exception">Exception thrown by the last attempt.</param>
    RetryDecision Decide(int attemptNumber, int maxRetries, Exception exception);
}
