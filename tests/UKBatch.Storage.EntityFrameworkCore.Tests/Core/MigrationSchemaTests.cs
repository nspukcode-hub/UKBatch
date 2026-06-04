using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// Real-migration schema shape on SQLite (Docker-free): the migration creates all 3 tables + the
/// expected indexes (incl. the unique <c>IX_BatchDefinitions_Source_Name</c>), and a migrate-then-dispatch
/// cycle works on the SAME pooled factory (a pooled context returns clean after
/// <c>MigrateAsync</c>). PostgreSQL parity lives in <c>PostgresJobStoreParityTests</c>
/// (<c>[Trait("Category", "RequiresDocker")]</c>).
/// </summary>
public sealed class MigrationSchemaTests
{
    [Fact]
    public async Task SqliteMigration_CreatesAllThreeTables()
    {
        await using var harness = await SqliteStoreHarness.CreateAsync();
        await using var db = await harness.NewContextAsync();

        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
            .ToListAsync();

        tables.Should().Contain(new[] { "JobExecutions", "BatchDefinitions", "ApprovalGates" });
    }

    [Fact]
    public async Task SqliteMigration_CreatesExpectedIndexes()
    {
        await using var harness = await SqliteStoreHarness.CreateAsync();
        await using var db = await harness.NewContextAsync();

        var indexes = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='index' AND name IS NOT NULL")
            .ToListAsync();

        // The plan: JobExecutions (Status,Enqueued)+(BatchDefId,Enqueued)+(JobName,Enqueued)+(BatchId);
        // BatchDefinitions unique (Source,Name) + (Source,Created); ApprovalGates (Status). 7 named indexes.
        indexes.Should().Contain("IX_BatchDefinitions_Source_Name", "the unique name index must be present and named");
        indexes.Should().Contain(i => i.Contains("JobExecutions", StringComparison.Ordinal) && i.Contains("Status", StringComparison.Ordinal));
        indexes.Should().Contain(i => i.Contains("JobExecutions", StringComparison.Ordinal) && i.Contains("BatchDefinitionId", StringComparison.Ordinal));
        indexes.Should().Contain(i => i.Contains("JobExecutions", StringComparison.Ordinal) && i.Contains("JobName", StringComparison.Ordinal));
        indexes.Should().Contain(i => i.Contains("ApprovalGates", StringComparison.Ordinal) && i.Contains("Status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SqliteMigration_UniqueSourceNameIndex_IsEnforced()
    {
        await using var harness = await SqliteStoreHarness.CreateAsync();
        var store = new EfBatchDefinitionStore(harness.Factory);
        await store.CreateAsync(TestData.BatchDef("id-1", "dup", Abstractions.Batches.BatchSource.Dashboard), CancellationToken.None);

        var act = async () => await store.CreateAsync(TestData.BatchDef("id-2", "dup", Abstractions.Batches.BatchSource.Dashboard), CancellationToken.None);
        await act.Should().ThrowAsync<UKBatch.Runtime.BatchDefinitionDuplicateNameException>(
            "the unique (Source,Name) index is live after migration");
    }

    [Fact]
    public async Task PooledFactory_MigrateThenDispatch_OnSamePool_CleanInstance()
    {
        // the PRODUCTION pooled factory (AddPooledDbContextFactory, NOT the harness's plain
        // factory). A real temp DB file persists across pooled-context creations. Migrate via the pool,
        // then dispatch (insert + read) via the pool — the migrate context must return clean to the pool.
        var dbPath = Path.Combine(Path.GetTempPath(), $"ukbatch-ef-test-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddUKBatch(_ => { });
            services.AddUKBatchEntityFrameworkCoreStores(o =>
            {
                o.UseSqlite($"DataSource={dbPath}");
                o.MigrateOnStartup = false;   // we migrate explicitly below
            });
            await using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IDbContextFactory<UKBatchDbContext>>();

            // Migrate on a pooled instance.
            await using (var migrateCtx = await factory.CreateDbContextAsync())
            {
                await migrateCtx.Database.MigrateAsync();
            }

            // Dispatch on (a) pooled instance(s) AFTER the migrate context returned to the pool.
            var store = provider.GetRequiredService<IJobStoreInternal>();
            await store.InsertAsync(TestData.Execution("e1", status: JobStatus.Pending), CancellationToken.None);
            await store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);

            var fetched = await store.GetAsync("e1", CancellationToken.None);
            fetched!.Status.Should().Be(JobStatus.Running, "migrate-then-dispatch on the same pool yields clean instances");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SqliteMigration_RoundTripsAcrossNewContext_Persists()
    {
        // A temp-file DB persists across context instances (models the restart-smoke durability).
        var dbPath = Path.Combine(Path.GetTempPath(), $"ukbatch-ef-persist-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddUKBatch(_ => { });
            services.AddUKBatchEntityFrameworkCoreStores(o => o.UseSqlite($"DataSource={dbPath}"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<UKBatchDbContext>>();

            await using (var ctx = await factory.CreateDbContextAsync())
            {
                await ctx.Database.MigrateAsync();
            }

            var store = new EfBatchDefinitionStore(factory);
            await store.CreateAsync(TestData.BatchDef("def-1", "persisted-batch"), CancellationToken.None);

            // A brand-new context (different instance) sees the persisted row.
            var fetched = await store.GetAsync("def-1", CancellationToken.None);
            fetched!.Name.Should().Be("persisted-batch");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SqliteMigration_HasMigrationHistoryTable_NotEnsureCreated()
    {
        // The adapter uses migrations (not EnsureCreated) — the __EFMigrationsHistory table must exist.
        await using var harness = await SqliteStoreHarness.CreateAsync();
        await using var db = await harness.NewContextAsync();

        var historyExists = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'")
            .ToListAsync();
        historyExists.Should().ContainSingle("migrations (not EnsureCreated) leave a history table");
    }
}
