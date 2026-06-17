using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Models;
using UKBatch.Runtime;
using UKBatch.Storage.EntityFrameworkCore.Entities;

namespace UKBatch.Storage.EntityFrameworkCore.Recovery;

/// <summary>
/// Runs ONCE on <see cref="StartAsync"/> to re-launch batch runs that a prior host crash/restart left
/// non-terminal, so a run continues from where it stopped instead of being abandoned (the orphan reaper
/// would otherwise only tombstone its interrupted execution rows). In-flight runs are resumed with
/// <c>ResumePolicy.ResumeForward</c> — completed steps are skipped, only the not-yet-finished work re-runs.
/// </summary>
/// <remarks>
/// <para><b>Ordering:</b> registered BEFORE <see cref="OrphanedExecutionReaper"/> so it re-dispatches a
/// resumed run's remaining steps before the reaper tombstones that run's prior (orphaned) execution rows.
/// The two are complementary, not racing: recovery touches the run row and dispatches NEW execution rows;
/// the reaper touches the OLD execution rows. The completion roll-up keeps only the latest attempt per
/// step, so the reaper's tombstone of a superseded row never mis-marks the resumed run as failed.</para>
/// <para><b>Opt-out:</b> <see cref="EfStorageOptions.ResumeInFlightRunsOnStartup"/> (default <c>true</c>)
/// disables automatic resume; the runs then stay in-flight as honest history.</para>
/// <para><b>Missing-table tolerance:</b> if the operator ran with <c>MigrateOnStartup=false</c> and never
/// applied migrations, the query throws a missing-table exception; we log and skip, the same posture as
/// the orphan reaper.</para>
/// <para><b>Single-node:</b> recovery re-launches a run on the node that booted; shared-DB multi-node
/// resume coordination (leader election) is not in this release.</para>
/// </remarks>
internal sealed class DurableRunRecovery : IHostedService
{
    private readonly IDbContextFactory<UKBatchDbContext> _factory;
    private readonly EfStorageOptions _options;
    private readonly IServiceProvider _services;   // resolve IJobRunner lazily (avoids a ctor cycle)
    private readonly ILogger<DurableRunRecovery> _logger;

    public DurableRunRecovery(
        IDbContextFactory<UKBatchDbContext> factory,
        EfStorageOptions options,
        IServiceProvider services,
        ILogger<DurableRunRecovery> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _options = options;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.ResumeInFlightRunsOnStartup)
        {
            _logger.LogDebug("Durable run recovery disabled (ResumeInFlightRunsOnStartup=false).");
            return;
        }

        List<BatchRunEntity> inflight;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            // In-flight = created but never completed (Status null, no completion timestamp).
            inflight = await db.BatchRuns
                .AsNoTracking()
                .Where(r => r.Status == null && r.CompletedAtUtc == null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Missing-table or any query failure is non-fatal: log and let the host start (the schema may
            // be absent if MigrateOnStartup=false and migrations were not applied).
            _logger.LogWarning(
                ex,
                "Durable run recovery query failed (the schema may be absent if MigrateOnStartup=false and migrations were not applied); skipping.");
            return;
        }

        if (inflight.Count == 0)
        {
            return;
        }

        var runner = _services.GetRequiredService<IJobRunner>();
        _logger.LogInformation(
            "Durable run recovery: re-launching {Count} in-flight batch run(s) with ResumeForward.", inflight.Count);

        foreach (var run in inflight)
        {
            try
            {
                // ResumeBatchAsync mirrors the trigger path: it awaits only the synchronous setup and runs
                // the batch fire-and-forget against the host's lifetime.
                await runner.ResumeBatchAsync(run.BatchId, ResumePolicy.ResumeForward, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One un-relaunchable run (e.g. its definition no longer exists) must not stop the others.
                // The reaper tombstones its orphan execution rows; the run record stays in-flight (honest:
                // "we could not resume this").
                _logger.LogWarning(
                    ex, "Durable run recovery: could not resume run {BatchId}; leaving it for the reaper.", run.BatchId);
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
