using UKBatch.Abstractions.Models;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// Monotonic rank of a <see cref="JobStatus"/> for stale-event rejection.
/// Shared by <c>LiveExecutionRow</c>, <c>Executions/Detail</c>, and <c>Batches/RunDetail</c> so the
/// rule has ONE source of truth.
/// </summary>
/// <remarks>
/// Under the up-to-4× hub fan-out plus reconnect re-subscribe, an older <see cref="JobStatus.Running"/>
/// event can arrive after a newer <see cref="JobStatus.Completed"/>. Callers reject the regression by
/// comparing ranks (lower rank ⇒ stale). Terminal states share rank <c>3</c> (none supersedes another);
/// unknown future statuses map to <c>-1</c> so they never win over a known state.
/// </remarks>
public static class JobStatusRank
{
    /// <summary>Returns the monotonic rank of <paramref name="s"/>; higher wins, <c>-1</c> for unknown.</summary>
    public static int Rank(JobStatus s) => s switch
    {
        JobStatus.Pending => 0,
        JobStatus.Scheduled => 0,
        JobStatus.Running => 1,
        JobStatus.AwaitingApproval => 1,
        JobStatus.Retrying => 1,
        JobStatus.Cancelling => 2,
        JobStatus.Completed => 3,
        JobStatus.Failed => 3,
        JobStatus.Cancelled => 3,
        JobStatus.Skipped => 3,
        _ => -1,
    };
}
