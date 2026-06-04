using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UKBatch.Storage.EntityFrameworkCore.DesignTime;

/// <summary>
/// Design-time factory for <see cref="SqliteUKBatchDbContext"/> (per-provider migration fallback). Used ONLY
/// by <c>dotnet ef</c> tooling targeting the SQLite context
/// (<c>dotnet ef migrations add ... -c SqliteUKBatchDbContext -o Migrations/Sqlite</c>). Never
/// constructs a runtime context. Disambiguated from the PostgreSQL factory by its distinct context
/// type — no env-var gating needed.
/// </summary>
/// <remarks>
/// The connection string is a design-time placeholder — <c>migrations add</c> does not connect.
/// </remarks>
public sealed class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqliteUKBatchDbContext>
{
    /// <inheritdoc/>
    public SqliteUKBatchDbContext CreateDbContext(string[] args)
    {
        var assemblyName = typeof(SqliteUKBatchDbContext).Assembly.FullName;
        var builder = new DbContextOptionsBuilder<SqliteUKBatchDbContext>();
        builder.UseSqlite(
            "Data Source=ukbatch-design.db",
            sl => sl.MigrationsAssembly(assemblyName));
        return new SqliteUKBatchDbContext(builder.Options);
    }
}
