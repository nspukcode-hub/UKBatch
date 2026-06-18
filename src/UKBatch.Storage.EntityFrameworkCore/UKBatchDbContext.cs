using Microsoft.EntityFrameworkCore;
using UKBatch.Storage.EntityFrameworkCore.Configuration;
using UKBatch.Storage.EntityFrameworkCore.Entities;

namespace UKBatch.Storage.EntityFrameworkCore;

/// <summary>
/// EF Core context for the UKBatch persistent stores. Five tables: <c>JobExecutions</c>,
/// <c>BatchDefinitions</c>, <c>ApprovalGates</c>, <c>BatchRuns</c>, <c>ScheduleStates</c>.
/// <c>BatchStep</c>s are JSON-embedded inside <c>BatchDefinitions</c> (no separate table) — recursion-safe
/// for nested parallel groups and forward-compatible via <c>BatchStep.Metadata</c> round-tripped verbatim.
/// </summary>
/// <remarks>
/// <para><b>Provider neutrality:</b> there is NO <c>OnConfiguring</c> provider switch. Provider
/// selection happens in DI (the pooled factory callback) so the SAME context targets both
/// PostgreSQL and SQLite — the migrations differ, not the context. <see cref="OnModelCreating"/>
/// detects the active provider (via <c>Database.IsSqlite()</c>) to pick the JSON column type
/// (<c>jsonb</c> vs <c>TEXT</c>) and whether the SQLite ISO-8601 <c>DateTimeOffset</c> converters
/// apply.</para>
/// <para><b>Schema versioning (v0.2 path):</b> the 3-table v0.1 schema is never restructured. v0.2
/// adds tables (e.g. <c>ScheduledJobs</c>, <c>MessageDedupe</c>, <c>WorkflowCheckpoints</c>), each a
/// new additive migration. <c>EnsureCreated()</c> is FORBIDDEN (no migration history, not evolvable);
/// apply migrations via <c>dotnet ef database update</c> or <c>MigrateOnStartup</c>.</para>
/// <para><b>Not <c>sealed</c> (per-provider migration fallback):</b> a single-context
/// migration path cannot host two providers' snapshots in one assembly (the second
/// provider's <c>migrations add</c> is refused). The fallback gives each provider its own
/// snapshot via a per-provider subclass (<see cref="PostgresUKBatchDbContext"/> /
/// <see cref="SqliteUKBatchDbContext"/>), so this base type must be inheritable. The two subclasses are
/// the ONLY permitted derivations.</para>
/// </remarks>
public class UKBatchDbContext : DbContext
{
    /// <summary>Constructs the context from DI-supplied options (provider + connection set by the factory callback).</summary>
    public UKBatchDbContext(DbContextOptions<UKBatchDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Subclass ctor seam (per-provider migration fallback). The provider-bound subclasses
    /// (<see cref="PostgresUKBatchDbContext"/> / <see cref="SqliteUKBatchDbContext"/>) pass their own
    /// <c>DbContextOptions&lt;TSelf&gt;</c> through the non-generic base options so EF keys a SEPARATE
    /// model snapshot per subclass type (the migration non-contamination guarantee). NOT used by the
    /// runtime stores, which inject <c>IDbContextFactory&lt;UKBatchDbContext&gt;</c>.
    /// </summary>
    protected UKBatchDbContext(DbContextOptions options)
        : base(options)
    {
    }

    internal DbSet<JobExecutionEntity> JobExecutions => Set<JobExecutionEntity>();

    internal DbSet<BatchDefinitionEntity> BatchDefinitions => Set<BatchDefinitionEntity>();

    internal DbSet<ApprovalGateEntity> ApprovalGates => Set<ApprovalGateEntity>();

    internal DbSet<BatchRunEntity> BatchRuns => Set<BatchRunEntity>();

    internal DbSet<ScheduleStateEntity> ScheduleStates => Set<ScheduleStateEntity>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Provider-specific column types. SQLite has no jsonb (TEXT) and no native DateTimeOffset
        // (ISO-8601 TEXT converters). PostgreSQL uses native jsonb + timestamptz.
        var isSqlite = Database.IsSqlite();
        var jsonType = isSqlite ? "TEXT" : "jsonb";

        modelBuilder.ApplyConfiguration(new JobExecutionConfiguration(jsonType, isSqlite));
        modelBuilder.ApplyConfiguration(new BatchDefinitionConfiguration(jsonType, isSqlite));
        modelBuilder.ApplyConfiguration(new ApprovalGateConfiguration(jsonType, isSqlite));
        // BatchRuns has no JSON column, so its configuration takes only the provider flag.
        modelBuilder.ApplyConfiguration(new BatchRunConfiguration(isSqlite));
        // ScheduleStates has no JSON column either — only the provider flag (for the SQLite date converter).
        modelBuilder.ApplyConfiguration(new ScheduleStateConfiguration(isSqlite));
    }
}
