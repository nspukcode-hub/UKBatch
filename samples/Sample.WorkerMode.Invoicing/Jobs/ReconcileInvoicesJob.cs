using System.Runtime.CompilerServices;
using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Invoicing.Jobs;

/// <summary>
/// LIVE intra-job parallelism demo: the canonical "SELECT first, then chew through the rows
/// on N concurrent workers" shape, as an <see cref="IPartitionedJob{TItem}"/>. Registered with
/// <c>.WithParallelism(3)</c> in <c>Program.cs</c> — the runtime owns the producer/consumer plumbing
/// (a bounded <c>Channel</c> + 3 consumer tasks, see Core's <c>ChannelFanout</c>); this class only
/// declares the source stream and the per-item work.
/// </summary>
/// <remarks>
/// Watch it run: trigger the <c>partitioned-demo</c> batch (step <c>ReconcileInvoices</c> on
/// <c>invoicing</c>) and (a) follow the run's Progress column on the dashboard counting to 12, and
/// (b) tail this worker's logs — the START lines arrive in overlapping waves of 3 (the worker count),
/// not one-by-one. <see cref="ProcessAsync"/> is called CONCURRENTLY and must stay thread-safe.
/// </remarks>
[Job(Name = "ReconcileInvoices")]
public sealed class ReconcileInvoicesJob : IPartitionedJob<ReconcileInvoicesJob.InvoiceRow>
{
    /// <summary>One "row" from the simulated SELECT.</summary>
    public sealed record InvoiceRow(int Id, decimal Amount);

    private const int TotalRows = 12;

    private readonly ILogger<ReconcileInvoicesJob> _logger;

    public ReconcileInvoicesJob(ILogger<ReconcileInvoicesJob> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The "SELECT": streams 12 pending invoices one by one (a real implementation would
    /// <c>await foreach</c> an EF Core <c>AsAsyncEnumerable()</c> / Dapper unbuffered query here).
    /// MUST stream — the bounded channel gives backpressure only if rows are yielded lazily.
    /// </summary>
    public async IAsyncEnumerable<InvoiceRow> SourceAsync(
        JobContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Progress.SetTotal(TotalRows);   // dashboard shows a live x/12 counter
        _logger.LogInformation(
            "ReconcileInvoices: SELECT (simulated) — streaming {Total} pending invoices to 3 workers.",
            TotalRows);

        for (var i = 1; i <= TotalRows; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false); // cursor-paced rows
            yield return new InvoiceRow(i, 100m + i);
        }
    }

    /// <summary>
    /// Per-row work — invoked from 3 concurrent worker tasks. The overlapping START log lines are the
    /// visible proof of the parallelism (waves of 3, ~700ms apart ⇒ 12 rows finish in ~3s, not ~8.4s).
    /// Results are ACCUMULATED (not saved row-by-row) — the single commit happens in FinalizeAsync.
    /// </summary>
    public async Task ProcessAsync(InvoiceRow item, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Count how many rows are being processed at the same instant. The work here is await-based
        // (Task.Delay), so the N concurrent workers cooperatively share thread-pool threads rather than
        // pinning one thread each — the live count is the honest proof of the parallelism. It rises to
        // WithParallelism(3) and stays there until the source is drained.
        var concurrent = Interlocked.Increment(ref _inFlight);
        _logger.LogInformation("ReconcileInvoices: START invoice #{Id} (amount {Amount}) — {Concurrent} workers busy now.", item.Id, item.Amount, concurrent);
        await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);     // simulated reconcile work
        _reconciled.Add(item);                                                                          // unit-of-work accumulation
        Interlocked.Decrement(ref _inFlight);
        _logger.LogInformation("ReconcileInvoices: DONE  invoice #{Id}.", item.Id);
    }

    // Live count of rows being processed concurrently — peaks at the configured worker count.
    private int _inFlight;

    // Unit-of-work accumulation: workers stash results here; FinalizeAsync commits ONCE.
    private readonly System.Collections.Concurrent.ConcurrentBag<InvoiceRow> _reconciled = new();

    /// <summary>
    /// The unit-of-work COMMIT point — runs exactly once after all 12 rows are processed, single-
    /// threaded. A real implementation does `_db.AddRange(results); await _db.SaveChangesAsync(ct);`
    /// (or a bulk insert) here, in ONE transaction. Never reached on a FailFast abort — nothing commits.
    /// </summary>
    public Task FinalizeAsync(JobContext context, CancellationToken cancellationToken)
    {
        var count = _reconciled.Count;
        _logger.LogInformation(
            "ReconcileInvoices: FINALIZE — bulk commit (simulated): AddRange({Count}) + SaveChanges in ONE transaction.",
            count);
        while (_reconciled.TryTake(out _)) { }   // drain so a reused instance never double-commits
        return Task.CompletedTask;
    }
}
