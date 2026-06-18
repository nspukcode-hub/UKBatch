using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Recovery;
using UKBatch.Storage.EntityFrameworkCore.Stores;

namespace UKBatch.Storage.EntityFrameworkCore;

/// <summary>
/// DI entry point for the EF Core storage adapter. <see cref="AddUKBatchEntityFrameworkCoreStores"/>
/// REPLACES the in-memory store descriptors registered by <c>AddUKBatch</c> with EF-backed singletons so
/// batch definitions, execution history, and pending approval RECORDS survive host restarts. Uses the
/// per-provider subclass factory + <see cref="SubclassFactoryFacade{T}"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EF Core stores over PostgreSQL (<c>opt.UsePostgres(cs)</c>) or SQLite
    /// (<c>opt.UseSqlite(cs)</c>), replacing the in-memory defaults. Idempotent:
    /// a second call is a no-op. Chain AFTER <c>AddUKBatch(...)</c>:
    /// <code>
    /// services.AddUKBatch(b => b.AddJob&lt;Foo&gt;())
    ///         .AddUKBatchEntityFrameworkCoreStores(o => o.UsePostgres(connectionString));
    /// </code>
    /// </summary>
    public static IServiceCollection AddUKBatchEntityFrameworkCoreStores(
        this IServiceCollection services, Action<EfStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Idempotency guard: if our facade is already registered, no-op (a re-call must not double-replace).
        if (services.Any(d => d.ServiceType == typeof(IDbContextFactory<UKBatchDbContext>)))
        {
            return services;
        }

        var opts = new EfStorageOptions();
        configure(opts);
        EfStorageOptionsValidator.ValidateOrThrow(opts);   // eager fail-fast at registration
        services.AddSingleton(opts);                       // resolved options for factory + reaper/guard
        services.AddSingleton<IValidateOptions<EfStorageOptions>, EfStorageOptionsValidator>();

        // 1) Per-provider pooled factory (short-lived per-op contexts) + the upcast facade so stores stay
        //    coded against IDbContextFactory<UKBatchDbContext>. The pool is shared by the
        //    dispatcher concurrency + reaper + (optional) migrator; each returns its instance cleanly
        //    via `await using`.
        var asm = typeof(UKBatchDbContext).Assembly.FullName;
        switch (opts.Provider)
        {
            case EfProvider.Postgres:
                services.AddPooledDbContextFactory<PostgresUKBatchDbContext>((_, b) =>
                    b.UseNpgsql(opts.ConnectionString, npg => npg.MigrationsAssembly(asm)));
                services.AddSingleton<IDbContextFactory<UKBatchDbContext>>(sp =>
                    new SubclassFactoryFacade<PostgresUKBatchDbContext>(
                        sp.GetRequiredService<IDbContextFactory<PostgresUKBatchDbContext>>()));
                break;
            case EfProvider.Sqlite:
                services.AddPooledDbContextFactory<SqliteUKBatchDbContext>((_, b) =>
                    b.UseSqlite(opts.ConnectionString, sl => sl.MigrationsAssembly(asm)));
                services.AddSingleton<IDbContextFactory<UKBatchDbContext>>(sp =>
                    new SubclassFactoryFacade<SqliteUKBatchDbContext>(
                        sp.GetRequiredService<IDbContextFactory<SqliteUKBatchDbContext>>()));
                break;
            default:
                // Unreachable (validator already threw), but keep the switch total.
                throw new InvalidOperationException(
                    "EfStorageOptions: no provider selected (call UsePostgres or UseSqlite).");
        }

        // 2) REMOVE the in-memory store descriptors. AddUKBatch registers them with PLAIN AddSingleton
        //    (NOT TryAdd), so RemoveAll is required — TryAdd would no-op against the existing descriptor.
        //    The shared JobExecutionWatchHub is NOT removed: it stays the singleton both stores compose
        //    via IJobExecutionWatchHub.
        services.RemoveAll<InMemoryJobStore>();
        services.RemoveAll<InMemoryApprovalGateStore>();
        services.RemoveAll<IJobStore>();
        services.RemoveAll<IJobStoreInternal>();
        services.RemoveAll<IJobExecutionReader>();
        services.RemoveAll<IJobExecutionWriter>();
        services.RemoveAll<IBatchDefinitionStore>();
        services.RemoveAll<IApprovalGateStore>();
        services.RemoveAll<IBatchRunStore>();

        // 3) Re-register the EF stores as singletons (same lifetime as the InMemory ones they replace).
        services.AddSingleton<EfJobStore>();
        services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<EfJobStore>());
        services.AddSingleton<IJobStoreInternal>(sp => sp.GetRequiredService<EfJobStore>());
        services.AddSingleton<IJobExecutionReader>(sp => sp.GetRequiredService<EfJobStore>());
        services.AddSingleton<IJobExecutionWriter>(sp => sp.GetRequiredService<EfJobStore>());
        services.AddSingleton<IBatchDefinitionStore, EfBatchDefinitionStore>();
        services.AddSingleton<IApprovalGateStore, EfApprovalGateStore>();
        services.AddSingleton<IBatchRunStore, EfBatchRunStore>();
        // Durable schedule watermarks. Core registers no default (catch-up is EF-only), so there is
        // no in-memory descriptor to RemoveAll — this is a plain add that the BatchScheduler resolves
        // optionally (it stays inactive when this store is absent).
        services.AddSingleton<IScheduleStateStore, EfScheduleStateStore>();

        // 4) Hosted services, in start order (hosted services start in registration order):
        //    migrator (when opted-in; creates the tables the others query) →
        //    durable run recovery (re-launch in-flight runs with ResumeForward) →
        //    schema guard (warn-log) →
        //    orphan reaper (tombstone what recovery did NOT relaunch).
        //    Recovery runs BEFORE the reaper so it re-dispatches a resumed run's remaining steps before
        //    the reaper tombstones that run's prior orphaned execution rows.
        if (opts.MigrateOnStartup)
        {
            services.AddSingleton<IHostedService, EfMigrationHostedService>();
        }
        services.AddSingleton<IHostedService, DurableRunRecovery>();
        services.AddSingleton<IHostedService, EfSchemaGuardHostedService>();
        services.AddSingleton<IHostedService, OrphanedExecutionReaper>();

        return services;
    }
}
