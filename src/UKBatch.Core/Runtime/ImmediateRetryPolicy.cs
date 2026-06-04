namespace UKBatch.Runtime;

/// <summary>Retries immediately (zero delay) until the retry budget is exhausted.</summary>
internal sealed class ImmediateRetryPolicy : IRetryPolicy
{
    /// <inheritdoc/>
    public RetryDecision Decide(int attemptNumber, int maxRetries, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return attemptNumber > maxRetries
            ? RetryDecision.Terminal
            : new RetryDecision { ShouldRetry = true, Delay = TimeSpan.Zero };
    }
}
