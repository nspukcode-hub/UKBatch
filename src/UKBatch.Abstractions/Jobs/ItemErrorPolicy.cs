namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Per-item error handling policy for partitioned (<see cref="IPartitionedJob{TItem}"/>) and
/// parallel-for-each (<see cref="JobContextParallelExtensions.ParallelForEachAsync{TItem}"/>) jobs.
/// <para>
/// Numeric values are stable across versions; new policies will be appended. Consumers switching
/// on this enum MUST include a <c>default:</c> arm so unknown future values are handled gracefully.
/// </para>
/// </summary>
public enum ItemErrorPolicy
{
    /// <summary>Cancel all workers on the first item failure; the job ends in <see cref="Models.JobStatus.Failed"/>.</summary>
    FailFast = 0,

    /// <summary>Log the failure (persist via <see cref="IJobProgress.ReportFailure()"/>) and continue processing remaining items.</summary>
    ContinueOnError = 1,

    /// <summary>Apply per-item retry; if all retries exhaust, treat as <see cref="ContinueOnError"/>.</summary>
    RetryThenContinue = 2,
}
