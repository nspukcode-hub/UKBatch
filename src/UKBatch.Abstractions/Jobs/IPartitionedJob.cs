namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Data-parallel job: streams items from a source and processes each item across N worker tasks
/// configured at registration. The runtime owns the producer-consumer plumbing; the implementer
/// only declares <see cref="SourceAsync"/> and <see cref="ProcessAsync"/>.
/// </summary>
/// <typeparam name="TItem">Item produced by the source and consumed by the processor.</typeparam>
public interface IPartitionedJob<TItem>
{
    /// <summary>
    /// Produces work items as an async stream and MUST honour <paramref name="cancellationToken"/>.
    /// For very large result sets, stream lazily (e.g. an open EF cursor) so the bounded channel's
    /// backpressure caps memory; for bounded sets it is perfectly fine to run ONE query, materialize
    /// the list (releasing the DB connection immediately), and <c>yield</c> from memory.
    /// </summary>
    IAsyncEnumerable<TItem> SourceAsync(JobContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a single item. Called concurrently from N workers; implementations MUST be thread-safe.
    /// Per-item failures are routed by <see cref="ItemErrorPolicy"/> configured on the registration.
    /// </summary>
    Task ProcessAsync(TItem item, JobContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Optional finalize hook — the unit-of-work commit point. Invoked EXACTLY ONCE after
    /// ALL items have been processed and every worker task has completed, on the SAME job instance and
    /// single-threaded (no <see cref="ProcessAsync"/> runs concurrently with it). The default
    /// implementation is a no-op, so existing implementations need not change.
    /// </summary>
    /// <remarks>
    /// <para><b>Accumulate-then-commit pattern:</b> have <see cref="ProcessAsync"/> compute results and
    /// stash them in a thread-safe instance field (e.g. <c>ConcurrentBag&lt;T&gt;</c>) WITHOUT touching
    /// a shared <c>DbContext</c> (it is not thread-safe); then AddRange + SaveChanges / bulk-insert
    /// HERE, in one transaction. The instance lives for the whole run, so instance state is safe.</para>
    /// <para><b>Failure semantics:</b> NOT invoked when the run aborts — an
    /// <see cref="ItemErrorPolicy.FailFast"/> item failure or a cancellation surfaces BEFORE this hook,
    /// preserving all-or-nothing (nothing committed). Under
    /// <see cref="ItemErrorPolicy.ContinueOnError"/> / <see cref="ItemErrorPolicy.RetryThenContinue"/>
    /// the fan-out completes normally and this hook runs — commit the successful subset (failures are
    /// counted on the execution's progress). An exception thrown here fails the job.</para>
    /// </remarks>
    Task FinalizeAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
