using Microsoft.EntityFrameworkCore;

namespace UKBatch.Storage.EntityFrameworkCore;

/// <summary>
/// SQLite-bound subclass of <see cref="UKBatchDbContext"/>. Adds NOTHING but its own CLR type identity
/// — which gives the SQLite provider its OWN <c>SqliteUKBatchDbContextModelSnapshot</c> (keyed to this
/// subclass type), so the two providers' migration snapshots never cross-contaminate. See
/// <see cref="PostgresUKBatchDbContext"/> remarks for the full rationale (per-provider migration fallback).
/// </summary>
public sealed class SqliteUKBatchDbContext : UKBatchDbContext
{
    /// <summary>Constructs the SQLite-bound context from DI-supplied options.</summary>
    public SqliteUKBatchDbContext(DbContextOptions<SqliteUKBatchDbContext> options)
        : base(options)
    {
    }
}
