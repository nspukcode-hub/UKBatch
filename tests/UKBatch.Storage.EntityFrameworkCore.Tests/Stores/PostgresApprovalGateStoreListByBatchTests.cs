using Microsoft.EntityFrameworkCore;
using Npgsql;
using FluentAssertions;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// PostgreSQL parity for <see cref="EfApprovalGateStore.ListByBatchAsync"/>: the SAME assertions as the
/// SQLite and InMemory store tests, run against a REAL PostgreSQL (Testcontainers) so the by-run query +
/// <c>timestamptz</c> ordering are exercised on the production provider. Gated by
/// <c>[Trait("Category","RequiresDocker")]</c> (filtered out of the Docker-free CI path). Each test uses
/// its own freshly-migrated database inside the shared container.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Trait("Category", "Parity")]
public sealed class PostgresApprovalGateStoreListByBatchTests : IClassFixture<PostgresContainerFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _fixture;
    private string _dbName = default!;
    private EfApprovalGateStore _store = default!;

    public PostgresApprovalGateStoreListByBatchTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _dbName = $"ukb_gate_listbybatch_{Guid.NewGuid():N}";
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

        _store = new EfApprovalGateStore(facade);
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
    public async Task ListByBatchAsync_ReturnsPendingAndDecided_ForTheRun_InStableOrder()
    {
        await _store.SaveAsync(TestData.Gate("g-pending", batchId: "run-1", pendingSinceUtc: T0.AddMinutes(2)), CancellationToken.None);
        await _store.SaveAsync(
            TestData.Gate("g-decided", batchId: "run-1", pendingSinceUtc: T0.AddMinutes(1), status: ApprovalRecordStatus.Decided, outcome: ApprovalRecordOutcome.Dismissed),
            CancellationToken.None);

        var gates = await _store.ListByBatchAsync("run-1", CancellationToken.None);

        gates.Select(g => g.ApprovalId).Should().Equal(new[] { "g-decided", "g-pending" },
            "pending AND decided are returned, ordered by PendingSinceUtc then ApprovalId");
        gates.Single(g => g.ApprovalId == "g-decided").Outcome.Should().Be(ApprovalRecordOutcome.Dismissed);
        gates.Single(g => g.ApprovalId == "g-pending").Status.Should().Be(ApprovalRecordStatus.Pending);
    }

    [Fact]
    public async Task ListByBatchAsync_UnknownBatch_ReturnsEmpty()
    {
        await _store.SaveAsync(TestData.Gate("g1", batchId: "run-1"), CancellationToken.None);
        (await _store.ListByBatchAsync("run-does-not-exist", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListByBatchAsync_IsRunScoped_DoesNotReturnAnotherRunsGates()
    {
        await _store.SaveAsync(TestData.Gate("g-a", batchId: "run-A"), CancellationToken.None);
        await _store.SaveAsync(TestData.Gate("g-b", batchId: "run-B"), CancellationToken.None);

        var gates = await _store.ListByBatchAsync("run-A", CancellationToken.None);
        gates.Select(g => g.ApprovalId).Should().Equal(new[] { "g-a" }, "the query is scoped to one run");
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
