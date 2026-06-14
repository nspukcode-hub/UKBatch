namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Declarative metadata for an <see cref="IJob"/> or <see cref="IPartitionedJob{TItem}"/>.
/// Consumed by attribute-based discovery during host startup.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class JobAttribute : Attribute
{
    /// <summary>
    /// Unique logical job name. If <c>null</c>, the discovery layer derives the name from the
    /// type's full name (namespace + type name).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Cron expression for scheduled execution; <c>null</c> means trigger-only (not scheduled).
    /// The field count is fixed by the configured cron format and defaults to SIX fields with
    /// seconds first (<c>sec min hour day month day-of-week</c>, e.g. <c>0 0 9 * * *</c> for
    /// daily at 09:00) — a classic five-field crontab expression is rejected at startup unless
    /// the host opts into the five-field format via its scheduling options.
    /// </summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Maximum retry attempts on failure, excluding the initial attempt. <c>null</c> means
    /// "inherit runtime default"; <c>0</c> means "explicitly no retry". Negative values are invalid.
    /// </summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Wall-clock timeout in seconds. <c>null</c> means "inherit runtime default"; <c>0</c> means
    /// "explicitly no timeout". Negative values are invalid.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Routing tags used in worker mode to filter dispatch to specific workers.
    /// Example: <c>["region:eu", "tier:critical"]</c>. <c>null</c> means no routing constraints.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Partitioned-job only: number of concurrent partition workers. <c>0</c> (default) means
    /// "use the runtime default" (<c>UKBatchOptions.DefaultPartitionWorkerCount</c>). Ignored for
    /// non-partitioned jobs. Equivalent to the fluent <c>WithParallelism(...)</c>.
    /// </summary>
    public int PartitionWorkerCount { get; init; }

    /// <summary>
    /// Partitioned-job only: per-item failure policy. Default <see cref="ItemErrorPolicy.FailFast"/>.
    /// Ignored for non-partitioned jobs. Equivalent to the fluent <c>WithItemErrorPolicy(...)</c>.
    /// <para>Note: <c>RetryThenContinue</c> set via attribute uses the job's <see cref="MaxRetries"/>
    /// for the per-item retry budget, mirroring the fluent path.</para>
    /// </summary>
    public ItemErrorPolicy ItemErrorPolicy { get; init; } = ItemErrorPolicy.FailFast;
}
