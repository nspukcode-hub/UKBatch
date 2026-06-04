namespace UKBatch.Runtime;

/// <summary>
/// Decision returned by <see cref="IRetryPolicy.Decide"/>: whether to retry and (if so) how long to wait.
/// </summary>
internal sealed record class RetryDecision
{
    /// <summary>True iff the orchestrator should re-enqueue.</summary>
    public required bool ShouldRetry { get; init; }

    /// <summary>Delay before re-enqueue; <see cref="TimeSpan.Zero"/> for immediate retry.</summary>
    public required TimeSpan Delay { get; init; }

    /// <summary>Singleton "give up" decision.</summary>
    public static RetryDecision Terminal { get; } = new() { ShouldRetry = false, Delay = TimeSpan.Zero };
}
