using Microsoft.EntityFrameworkCore;

namespace UKBatch.Storage.EntityFrameworkCore;

/// <summary>
/// PostgreSQL-bound subclass of <see cref="UKBatchDbContext"/>. Adds NOTHING but its own CLR type
/// identity — which gives the PostgreSQL provider its OWN <c>PostgresUKBatchDbContextModelSnapshot</c>
/// (keyed to this subclass type), so the two providers' migration snapshots never cross-contaminate.
/// </summary>
/// <remarks>
/// <para>Rationale (per-provider migration fallback): EF's design-time tooling writes ONE model snapshot per
/// context type. A single shared <see cref="UKBatchDbContext"/> targeted by both providers shares ONE
/// snapshot and ONE migration-name registry — empirically, the second provider's <c>migrations add</c>
/// then refuses the name and produces no migration. Two subclasses give each provider an independent
/// snapshot + migration set, which is unambiguous.</para>
/// <para>All mapping lives in <see cref="UKBatchDbContext.OnModelCreating"/> (inherited unchanged); the
/// base detects the active provider via <c>Database.IsSqlite()</c>, so this subclass needs no model
/// overrides. The runtime stores stay coded against <see cref="UKBatchDbContext"/> — a tiny DI facade
/// adapts the subclass factory to <c>IDbContextFactory&lt;UKBatchDbContext&gt;</c>.</para>
/// </remarks>
public sealed class PostgresUKBatchDbContext : UKBatchDbContext
{
    /// <summary>Constructs the PostgreSQL-bound context from DI-supplied options.</summary>
    public PostgresUKBatchDbContext(DbContextOptions<PostgresUKBatchDbContext> options)
        : base(options)
    {
    }
}
