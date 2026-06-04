using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UKBatch.Storage.EntityFrameworkCore.Recovery;

/// <summary>
/// Emits the MANDATORY missing-table fast-fail warn-log at host start when
/// <see cref="EfStorageOptions.MigrateOnStartup"/> is <c>false</c>. The
/// reaper's defensive missing-table tolerance protects only the reaper — the FIRST live
/// <c>CreateAsync</c>/<c>InsertAsync</c> would otherwise throw a runtime <c>DbException</c> if the
/// operator skipped <c>dotnet ef database update</c>. This warning tells operators they own the schema.
/// </summary>
internal sealed class EfSchemaGuardHostedService : IHostedService
{
    private readonly EfStorageOptions _options;
    private readonly ILogger<EfSchemaGuardHostedService> _logger;

    public EfSchemaGuardHostedService(EfStorageOptions options, ILogger<EfSchemaGuardHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.MigrateOnStartup)
        {
            _logger.LogWarning(
                "EF storage: MigrateOnStartup is OFF; you own schema creation via `dotnet ef database update`. "
                + "If the tables are absent, the first job dispatch will throw.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
