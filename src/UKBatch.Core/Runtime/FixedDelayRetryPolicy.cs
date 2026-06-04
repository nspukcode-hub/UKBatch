namespace UKBatch.Runtime;

/// <summary>Retries after a fixed wait until the retry budget is exhausted.</summary>
internal sealed class FixedDelayRetryPolicy : IRetryPolicy
{
    private readonly TimeSpan _delay;

    /// <summary>Constructs the policy with a fixed inter-attempt delay.</summary>
    public FixedDelayRetryPolicy(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be >= TimeSpan.Zero.");
        }
        _delay = delay;
    }

    /// <inheritdoc/>
    public RetryDecision Decide(int attemptNumber, int maxRetries, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return attemptNumber > maxRetries
            ? RetryDecision.Terminal
            : new RetryDecision { ShouldRetry = true, Delay = _delay };
    }
}
