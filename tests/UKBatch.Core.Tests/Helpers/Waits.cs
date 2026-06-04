namespace UKBatch.Core.Tests.Helpers;

/// <summary>
/// Hard-bounded polling helpers for async test conditions. Every wait has a strict upper bound
/// so failure to satisfy is reported as a test failure rather than a hung run.
/// </summary>
internal static class Waits
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns true or <paramref name="timeout"/>
    /// elapses. Returns true if the predicate succeeded, false if the timeout fired.
    /// </summary>
    public static async Task<bool> ForAsync(Func<bool> predicate, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(interval).ConfigureAwait(false);
        }
        return predicate();
    }

    public static async Task<bool> ForAsync(Func<Task<bool>> predicate, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return true;
            }
            await Task.Delay(interval).ConfigureAwait(false);
        }
        return await predicate().ConfigureAwait(false);
    }
}
