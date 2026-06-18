using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UKBatch.Storage.EntityFrameworkCore.Recovery;

/// <summary>
/// Applies pending EF migrations on host start when <see cref="EfStorageOptions.MigrateOnStartup"/> is
/// <c>true</c> (dev/demo convenience; production applies migrations out of band with
/// <c>dotnet ef database update</c>).
/// </summary>
/// <remarks>
/// Migrates in <see cref="IHostedLifecycleService.StartingAsync"/>, NOT <see cref="StartAsync"/>: the host
/// runs every <c>StartingAsync</c> before ANY hosted service's <c>StartAsync</c>, so the schema is in
/// place before the runtime's own hosted service (the core host that drives the batch scheduler) starts
/// and scans the definition store. That core host is registered ahead of this storage adapter, so a plain
/// <c>StartAsync</c> migrator would run too LATE — the scheduler's startup scan would hit a not-yet-migrated
/// table or a missing column on a definition table a migration is about to alter. The durable run recovery
/// and orphan reaper run later, in the <c>StartAsync</c> phase, after this has created the tables they
/// query. <see cref="DbContext.Database"/>'s <c>MigrateAsync</c> on a pooled context is supported (the
/// context returns to the pool cleanly via the <c>await using</c> scope); <c>EnsureCreated()</c> is never
/// used (no migration history, not evolvable).
/// </remarks>
internal sealed class EfMigrationHostedService : IHostedLifecycleService
{
    private readonly IDbContextFactory<UKBatchDbContext> _factory;
    private readonly ILogger<EfMigrationHostedService> _logger;

    public EfMigrationHostedService(
        IDbContextFactory<UKBatchDbContext> factory,
        ILogger<EfMigrationHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Applies pending migrations before any hosted service's <c>StartAsync</c> runs, so a consumer's
    /// runtime host (registered ahead of this adapter) never scans an un-migrated schema at startup.
    /// </summary>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EF storage: applying pending migrations on startup (MigrateOnStartup=true).");
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
