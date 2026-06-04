namespace UKBatch.Runtime;

/// <summary>
/// Exponential backoff with a configurable base delay, multiplier, and cap.
/// Default registration: base 1s, factor 2.0, cap 1 minute.
/// </summary>
internal sealed class ExponentialRetryPolicy : IRetryPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly double _factor;
    private readonly TimeSpan _maxDelay;

    /// <summary>Constructs the policy.</summary>
    public ExponentialRetryPolicy(TimeSpan baseDelay, double factor, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);
        _baseDelay = baseDelay;
        _factor = factor;
        _maxDelay = maxDelay;
    }

    /// <inheritdoc/>
    public RetryDecision Decide(int attemptNumber, int maxRetries, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (attemptNumber > maxRetries)
        {
            return RetryDecision.Terminal;
        }
        var delayMs = _baseDelay.TotalMilliseconds * Math.Pow(_factor, Math.Max(0, attemptNumber - 1));
        var delay = TimeSpan.FromMilliseconds(Math.Min(delayMs, _maxDelay.TotalMilliseconds));
        return new RetryDecision { ShouldRetry = true, Delay = delay };
    }
}
