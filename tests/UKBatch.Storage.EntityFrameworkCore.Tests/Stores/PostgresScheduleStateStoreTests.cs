using Microsoft.EntityFrameworkCore;
using Npgsql;
using FluentAssertions;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// PostgreSQL parity for <see cref="EfScheduleStateStore"/>: the watermark round-trips through GetAll and
/// the store is <b>monotonic</b> (an older write never regresses a newer one) on a REAL PostgreSQL
/// (Testcontainers), so the <c>timestamptz</c> column + the monotonic upsert are exercised on the
/// production provider. Gated by <c>[Trait("Category","RequiresDocker")]</c> (filtered out of the
/// Docker-free CI path). Each test uses its own freshly-migrated database inside the shared container.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Trait("Category", "Parity")]
public sealed class PostgresScheduleStateStoreTests : IClassFixture<PostgresContainerFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _fixture;
    private string _dbName = default!;
    private EfScheduleStateStore _store = default!;

    public PostgresScheduleStateStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _dbName = $"ukb_schedstate_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{_dbName}\"";
            await create.ExecuteNonQueryAsync();
        }

        var connString = new NpgsqlConnectionStringBuilder(_fixture.AdminConnectionString) { Database = _dbName }.ConnectionString;
        var facade = new SubclassFactoryFacade<PostgresUKBatchDbContext>(new PostgresFactory(connString));

        await using (var db = await facade.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }

        _store = new EfScheduleStateStore(facade);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RecordFiredAsync_Inserts_AndRoundTrips()
    {
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);

        var all = await _store.GetAllAsync(CancellationToken.None);
        all.Should().ContainKey("def-1");
        all["def-1"].Should().Be(T0, "the watermark round-trips through timestamptz");
    }

    [Fact]
    public async Task RecordFiredAsync_IsMonotonic_OlderWriteDoesNotRegress()
    {
        await _store.RecordFiredAsync("def-1", T0.AddHours(1), CancellationToken.None);
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);   // older

        var all = await _store.GetAllAsync(CancellationToken.None);
        all["def-1"].Should().Be(T0.AddHours(1), "an older write must not regress the watermark");
    }

    [Fact]
    public async Task RecordFiredAsync_MultipleDefinitions_KeptIndependent()
    {
        await _store.RecordFiredAsync("def-1", T0, CancellationToken.None);
        await _store.RecordFiredAsync("def-2", T0.AddDays(1), CancellationToken.None);

        var all = await _store.GetAllAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        all["def-1"].Should().Be(T0);
        all["def-2"].Should().Be(T0.AddDays(1));
    }

    [Fact]
    public async Task RecordFiredAsync_ConcurrentWriters_ConvergeOnNewest_NoRegressionNoThrow()
    {
        // All writers target a definition that does not exist yet, so they race on the INSERT (one wins,
        // the losers hit a unique violation and resolve via the atomic monotonic advance). Surviving this
        // race is the store's whole reason to exist, so it must be exercised under TRUE concurrency: the
        // final watermark MUST be the newest occurrence and no exception may escape. A read-modify-write
        // upsert would let a later-committing older write regress the watermark here.
        const int writers = 20;
        var occurrences = Enumerable.Range(0, writers).Select(i => T0.AddMinutes(i)).ToArray();
        var newest = occurrences[^1];

        await Task.WhenAll(occurrences.Select(o => Task.Run(() => _store.RecordFiredAsync("def-1", o, CancellationToken.None))));

        var all = await _store.GetAllAsync(CancellationToken.None);
        all["def-1"].Should().Be(newest, "concurrent writers must converge on the newest occurrence, never regress");
    }

    /// <summary>Plain (non-pooled) factory over a fixed PG connection string — mirrors the parity harness factory.</summary>
    private sealed class PostgresFactory : IDbContextFactory<PostgresUKBatchDbContext>
    {
        private readonly string _connString;
        public PostgresFactory(string connString) => _connString = connString;

        public PostgresUKBatchDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<PostgresUKBatchDbContext>().UseNpgsql(_connString).Options);

        public Task<PostgresUKBatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
