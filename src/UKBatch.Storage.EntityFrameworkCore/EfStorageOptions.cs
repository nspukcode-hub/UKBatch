namespace UKBatch.Storage.EntityFrameworkCore;

/// <summary>
/// Configuration for the EF Core storage adapter: provider selection + startup behaviors. Bound and
/// mutated by the <c>AddUKBatchEntityFrameworkCoreStores</c> configure callback.
/// </summary>
/// <remarks>
/// Provider selection is exclusive — one provider per deployment. Calling both
/// <see cref="UsePostgres"/> and <see cref="UseSqlite"/> is last-wins.
/// </remarks>
public sealed class EfStorageOptions
{
    internal EfProvider Provider { get; private set; } = EfProvider.None;

    internal string ConnectionString { get; private set; } = "";

    /// <summary>
    /// Apply pending migrations on host start (dev convenience). Production: run
    /// <c>dotnet ef database update</c>. <c>EnsureCreated()</c> is NOT used (no migration history).
    /// </summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>
    /// Grace window before an interrupted (non-terminal) execution is reaped to <c>Failed</c>.
    /// Default 2 min — covers a normal graceful restart in flight. <see cref="TimeSpan.Zero"/> disables
    /// the reaper (both the execution sweep and the orphaned-gate sweep).
    /// </summary>
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Selects the PostgreSQL (Npgsql) provider with the given connection string.</summary>
    public EfStorageOptions UsePostgres(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Provider = EfProvider.Postgres;
        ConnectionString = connectionString;
        return this;
    }

    /// <summary>Selects the SQLite provider with the given connection string.</summary>
    public EfStorageOptions UseSqlite(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Provider = EfProvider.Sqlite;
        ConnectionString = connectionString;
        return this;
    }
}

/// <summary>The persistence provider selected via <see cref="EfStorageOptions"/>.</summary>
internal enum EfProvider
{
    None = 0,
    Postgres = 1,
    Sqlite = 2,
}
