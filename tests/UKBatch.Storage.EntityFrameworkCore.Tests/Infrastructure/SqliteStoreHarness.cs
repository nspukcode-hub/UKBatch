using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Storage.EntityFrameworkCore;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// Docker-free SQLite harness: a real <see cref="SqliteUKBatchDbContext"/> over an in-memory SQLite
/// connection, migrated via the REAL EF migration (not <c>EnsureCreated</c>), exposing the same
/// <see cref="IDbContextFactory{TContext}"/> seam the production stores inject (via
/// <see cref="SubclassFactoryFacade{T}"/>, so the facade upcast is exercised too).
/// </summary>
/// <remarks>
/// <para>A SQLite <c>:memory:</c> database is bound to a single open connection — closing the connection
/// drops the schema. We hold ONE open connection for the harness lifetime so every context the factory
/// creates shares the same in-memory DB (this also models a connection-pooled real database for the
/// stores' per-method <c>await using</c> contexts).</para>
/// <para>The factory is NOT pooled (production uses <c>AddPooledDbContextFactory</c>); a plain factory
/// over a shared connection is the faithful Docker-free equivalent and keeps the test DB alive. The
/// pooled-instance clean-state behavior is asserted separately where it matters
/// (<c>MigrationSchemaTests</c>).</para>
/// </remarks>
internal sealed class SqliteStoreHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private SqliteStoreHarness(SqliteConnection connection, IDbContextFactory<UKBatchDbContext> factory, FakeTimeProvider clock)
    {
        _connection = connection;
        Factory = factory;
        Clock = clock;
    }

    /// <summary>The base-typed factory the stores inject (a <see cref="SubclassFactoryFacade{T}"/> over the SQLite subclass).</summary>
    public IDbContextFactory<UKBatchDbContext> Factory { get; }

    /// <summary>Deterministic clock the stores/reaper consume.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>Creates a migrated, ready-to-use SQLite harness with a deterministic clock.</summary>
    public static async Task<SqliteStoreHarness> CreateAsync(FakeTimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);

        var inner = new SqliteSubclassFactory(connection);
        var facade = new SubclassFactoryFacade<SqliteUKBatchDbContext>(inner);

        // Apply the REAL migration (mirrors EfMigrationHostedService / production), not EnsureCreated.
        await using (var db = await facade.CreateDbContextAsync().ConfigureAwait(false))
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
        }

        return new SqliteStoreHarness(connection, facade, clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Opens a raw context for direct entity inspection (test-only assertions on stored rows).</summary>
    public Task<UKBatchDbContext> NewContextAsync() => Factory.CreateDbContextAsync();

    /// <summary>
    /// A plain (non-pooled) <see cref="IDbContextFactory{T}"/> over the shared in-memory connection.
    /// Each created context targets the same DB; disposal does not close the shared connection.
    /// </summary>
    private sealed class SqliteSubclassFactory : IDbContextFactory<SqliteUKBatchDbContext>
    {
        private readonly SqliteConnection _connection;

        public SqliteSubclassFactory(SqliteConnection connection) => _connection = connection;

        public SqliteUKBatchDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<SqliteUKBatchDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new SqliteUKBatchDbContext(options);
        }

        public Task<SqliteUKBatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
