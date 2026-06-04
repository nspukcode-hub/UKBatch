using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage.EntityFrameworkCore.Recovery;

/// <summary>
/// Runs ONCE on <see cref="StartAsync"/> to reconcile rows left non-terminal by a prior host crash/restart.
/// Two sweeps in one pass, same grace window:
/// <list type="number">
///   <item><b>SWEEP-1</b> — <c>JobExecutions</c> still non-terminal past the grace window → <c>Failed</c>
///         with a documented <c>LastError</c>. There is no durable workflow RESUME in v0.1; these rows
///         would otherwise sit non-terminal forever.</item>
///   <item><b>SWEEP-2</b> — <c>ApprovalGates</c> still <c>Pending</c> past the grace window →
///         <c>(Decided, Interrupted, "&lt;reaper&gt;", now, note)</c> so the store-aware merge no
///         longer resurrects a dead gate as a permanent ghost.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Grace window:</b> <see cref="EfStorageOptions.OrphanGracePeriod"/> (default 2 min) protects a
/// concurrent healthy node's just-enqueued rows in a shared-DB topology (the reaper only runs at THIS
/// node's startup; a crash-frozen row stays non-terminal past the window, a healthy row keeps
/// advancing). <see cref="TimeSpan.Zero"/> DISABLES both sweeps — shared-DB operators with long human
/// decision windows raise the grace period or set Zero.</para>
/// <para><b>Sanctioned state-machine bypass:</b> SWEEP-1 writes <c>Status=Failed</c> DIRECTLY on
/// the entity, bypassing <see cref="JobStatusTransitions.Validate"/>, because <c>Retrying→Failed</c> has
/// no legal edge yet <c>Failed</c> is the correct terminal for an interrupted orphan. This is the ONE
/// sanctioned validation bypass; it is isolated to this hosted service, never exposed on any store/writer
/// seam.</para>
/// <para><b>Missing-table tolerance:</b> if the operator ran with <c>MigrateOnStartup=false</c> and never
/// applied migrations, the sweep queries throw a missing-table <c>DbException</c>; we log and skip
/// (defense-in-depth — the live dispatch path's fast-fail is the schema-guard warn-log).</para>
/// </remarks>
internal sealed class OrphanedExecutionReaper : IHostedService
{
    private const string ExecutionReason =
        "Interrupted by host restart (no durable workflow resume in v0.1).";
    private const string GateReason =
        "Interrupted by host restart (batch did not survive; no durable resume in v0.1).";
    private const string ReaperSentinel = "<reaper>";

    private readonly IDbContextFactory<UKBatchDbContext> _factory;
    private readonly EfStorageOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrphanedExecutionReaper> _logger;

    public OrphanedExecutionReaper(
        IDbContextFactory<UKBatchDbContext> factory,
        EfStorageOptions options,
        TimeProvider clock,
        ILogger<OrphanedExecutionReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.OrphanGracePeriod <= TimeSpan.Zero)
        {
            _logger.LogDebug("OrphanGracePeriod is Zero — orphan reaper disabled.");
            return;
        }

        var now = _clock.GetUtcNow();
        var cutoff = now - _options.OrphanGracePeriod;

        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await SweepExecutionsAsync(db, cutoff, now, cancellationToken).ConfigureAwait(false);
            await SweepApprovalGatesAsync(db, cutoff, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Missing-table or any reconciliation failure is non-fatal: log and let the host start.
            _logger.LogWarning(
                ex,
                "Orphan reaper sweep failed (the schema may be absent if MigrateOnStartup=false and migrations were not applied); skipping.");
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // --- Sweep 1: orphaned executions. Direct Status write — the ONE sanctioned bypass. ---
    private async Task SweepExecutionsAsync(UKBatchDbContext db, DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct)
    {
        var orphans = await db.JobExecutions
            .Where(e => e.Status != JobStatus.Completed
                        && e.Status != JobStatus.Failed
                        && e.Status != JobStatus.Cancelled
                        && e.EnqueuedAtUtc < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);

        if (orphans.Count == 0)
        {
            return;
        }

        foreach (var orphan in orphans)
        {
            // SANCTIONED BYPASS: direct terminal write, NOT through JobStatusTransitions.Validate
            // (Retrying→Failed has no legal edge but Failed is the honest terminal for an orphan).
            orphan.Status = JobStatus.Failed;
            orphan.LastError = ExecutionReason;
            orphan.CompletedAtUtc = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Reaped {Count} orphaned executions interrupted by a prior restart.", orphans.Count);
    }

    // --- Sweep 2: orphaned approval-gate records. ---
    private async Task SweepApprovalGatesAsync(UKBatchDbContext db, DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct)
    {
        var orphanGates = await db.ApprovalGates
            .Where(g => g.Status == ApprovalRecordStatus.Pending && g.PendingSinceUtc < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);

        if (orphanGates.Count == 0)
        {
            return;
        }

        foreach (var gate in orphanGates)
        {
            // Terminal transition (Pending,null) → (Decided, Interrupted, "<reaper>", now, note).
            // Gate records have no state machine; this is a direct administrative write.
            gate.Status = ApprovalRecordStatus.Decided;
            gate.Outcome = ApprovalRecordOutcome.Interrupted;
            gate.DecidedBy = ReaperSentinel;
            gate.DecidedAtUtc = now;
            gate.Note = GateReason;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Reaped {GateCount} orphaned approval gates interrupted by a prior restart.", orphanGates.Count);
    }
}
