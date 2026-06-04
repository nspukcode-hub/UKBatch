using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Parity;

/// <summary>
/// Parity: runs the shared suite against the EF <see cref="EfJobStore"/> over a REAL PostgreSQL
/// (Testcontainers). This is the path that exercises the <c>EF.Functions.ILike</c> branch
/// — the one query translation SQLite/InMemory cannot cover — plus <c>timestamptz</c>
/// ordering and the <c>jsonb</c>-via-TEXT column on the real provider. Gated by
/// <c>[Trait("Category","RequiresDocker")]</c>; each test gets its own freshly-migrated database inside
/// the shared container.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Trait("Category", "Parity")]
public sealed class PostgresJobStoreParityTests : JobStoreParityTestBase, IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;
    private string? _dbName;

    public PostgresJobStoreParityTests(PostgresContainerFixture fixture) => _fixture = fixture;

    protected override async Task<IJobStoreInternal> CreateStoreAsync(FakeTimeProvider clock)
    {
        // Fresh, uniquely-named database per test for full isolation inside the shared container.
        _dbName = $"ukb_parity_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{_dbName}\"";
            await create.ExecuteNonQueryAsync();
        }

        var connString = new NpgsqlConnectionStringBuilder(_fixture.AdminConnectionString) { Database = _dbName }.ConnectionString;
        var facade = new SubclassFactoryFacade<PostgresUKBatchDbContext>(new PostgresParityFactory(connString));

        // Apply the REAL PostgreSQL migration (not EnsureCreated) — mirrors production.
        await using (var db = await facade.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }

        return new EfJobStore(
            facade,
            new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance),
            clock,
            NullLogger<EfJobStore>.Instance);
    }

    protected override async Task DisposeStoreAsync()
    {
        if (_dbName is null)
        {
            return;
        }

        // Return pooled connections so the DROP is not blocked, then drop the per-test database (FORCE
        // terminates any leftover backends). Best-effort — the container teardown is the backstop.
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Plain (non-pooled) factory over a fixed PG connection string — the Docker equivalent of the SQLite harness factory.</summary>
    private sealed class PostgresParityFactory : IDbContextFactory<PostgresUKBatchDbContext>
    {
        private readonly string _connString;

        public PostgresParityFactory(string connString) => _connString = connString;

        public PostgresUKBatchDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<PostgresUKBatchDbContext>().UseNpgsql(_connString).Options);

        public Task<PostgresUKBatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
