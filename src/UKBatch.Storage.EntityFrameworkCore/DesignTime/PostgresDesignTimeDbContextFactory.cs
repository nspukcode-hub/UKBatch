using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UKBatch.Storage.EntityFrameworkCore.DesignTime;

/// <summary>
/// Design-time factory for <see cref="PostgresUKBatchDbContext"/> (per-provider migration fallback). Used ONLY
/// by <c>dotnet ef</c> tooling targeting the PostgreSQL context
/// (<c>dotnet ef migrations add ... -c PostgresUKBatchDbContext -o Migrations/Postgres</c>). Never
/// constructs a runtime context. Disambiguated from the SQLite factory by its distinct context type —
/// no env-var gating needed.
/// </summary>
/// <remarks>
/// The connection string is a design-time placeholder — <c>migrations add</c> does not connect.
/// </remarks>
public sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresUKBatchDbContext>
{
    /// <inheritdoc/>
    public PostgresUKBatchDbContext CreateDbContext(string[] args)
    {
        var assemblyName = typeof(PostgresUKBatchDbContext).Assembly.FullName;
        var builder = new DbContextOptionsBuilder<PostgresUKBatchDbContext>();
        builder.UseNpgsql(
            "Host=localhost;Database=ukbatch_design;Username=design;Password=design",
            npg => npg.MigrationsAssembly(assemblyName));
        return new PostgresUKBatchDbContext(builder.Options);
    }
}
