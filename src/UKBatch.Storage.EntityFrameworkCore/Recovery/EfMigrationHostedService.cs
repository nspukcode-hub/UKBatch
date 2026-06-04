using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UKBatch.Storage.EntityFrameworkCore.Recovery;

/// <summary>
/// Applies pending EF migrations on host start when <see cref="EfStorageOptions.MigrateOnStartup"/> is
/// <c>true</c> (dev/demo convenience; production runs <c>dotnet ef database update</c>). Registered
/// BEFORE <see cref="OrphanedExecutionReaper"/> so the reaper queries tables this creates.
/// </summary>
/// <remarks>
/// <see cref="DbContext.Database"/>'s <c>MigrateAsync</c> on a pooled context is supported; the context
/// returns to the pool cleanly via the <c>await using</c> scope. <c>EnsureCreated()</c> is
/// never used (no migration history, not evolvable).
/// </remarks>
internal sealed class EfMigrationHostedService : IHostedService
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

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EF storage: applying pending migrations on startup (MigrateOnStartup=true).");
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
