namespace UKBatch.Runtime;

// Test-only IRetryPolicy strategies. Production wires ExponentialRetryPolicy; these instant and
// fixed-delay variants exist purely to make retry timing deterministic in tests, so they live in
// the test assembly rather than shipping inside UKBatch.Core. They implement the internal
// IRetryPolicy through InternalsVisibleTo and keep the UKBatch.Runtime namespace so call sites
// resolve unchanged.

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
