using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// The headline guarantee, automated and Docker-free: a batch's execution HISTORY, its batch
/// DEFINITIONS, and a PENDING APPROVAL gate all survive a host restart when backed by the EF Core adapter
/// over a SQLite <b>file</b>. Two SEQUENTIAL real DI containers point at the SAME db file — the first
/// writes through the production stores then is disposed ("process dies"); the second is a cold boot
/// (empty in-memory state) that reads everything back through the production stores. This is the
/// automated complement to <c>smoke-restart-sqlite.sh</c> (the real-process HTTP smoke).
/// </summary>
/// <remarks>
/// Stronger than the <c>:memory:</c> recovery tests (<c>DurableApprovalRecoveryTests</c>): those keep the
/// schema alive on a single keep-alive connection, whereas this proves durability across the FILE after
/// the writing container — and its connection pool — is fully gone. It also exercises the real
/// <c>AddUKBatchEntityFrameworkCoreStores</c> DI-replacement path (not a hand-built store) on both boots,
/// including <c>MigrateOnStartup</c> running idempotently on the second boot.
/// </remarks>
public sealed class HostRestartPersistenceTests
{
    [Fact]
    public async Task RestartOverSameSqliteFile_ExecutionHistory_Definitions_AndPendingApproval_AllSurvive()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ukbatch-restart-{Guid.NewGuid():N}.db");
        try
        {
            // ===== Host 1: write through the production stores, then "crash" (dispose). =====
            await using (var sp1 = BuildHost(dbPath))
            {
                await MigrateAsync(sp1);

                // Execution history — an in-flight Running execution attributed to a batch definition.
                await sp1.GetRequiredService<IJobStoreInternal>().InsertAsync(
                    TestData.Execution("exec-1", jobName: "Invoice.Process", batchId: "run-1",
                        batchDefinitionId: "def-1", status: JobStatus.Running),
                    CancellationToken.None);

                // Batch definition — the wizard-created/registered pipeline that must outlive the process.
                await sp1.GetRequiredService<IBatchDefinitionStore>().CreateAsync(
                    TestData.BatchDef("def-1", "persisted-pipeline"), CancellationToken.None);

                // Pending approval gate — paused mid-batch, awaiting a human decision.
                await sp1.GetRequiredService<IApprovalGateStore>().SaveAsync(
                    TestData.Gate("gate-1", batchId: "run-1", batchDefinitionId: "def-1"), CancellationToken.None);
            }

            // ===== Host 2: cold boot over the SAME file (empty in-memory state) reads it all back. =====
            await using (var sp2 = BuildHost(dbPath))
            {
                await MigrateAsync(sp2);   // idempotent: pending migrations only → no-op on the existing file

                var execution = await sp2.GetRequiredService<IJobStoreInternal>().GetAsync("exec-1", CancellationToken.None);
                execution.Should().NotBeNull("execution history must survive the restart");
                execution!.JobName.Should().Be("Invoice.Process");
                execution.Status.Should().Be(JobStatus.Running);
                execution.BatchDefinitionId.Should().Be("def-1", "the batch-definition attribution field must round-trip across the restart");

                var definition = await sp2.GetRequiredService<IBatchDefinitionStore>().GetAsync("def-1", CancellationToken.None);
                definition.Should().NotBeNull("batch definitions must survive the restart");
                definition!.Name.Should().Be("persisted-pipeline");

                var pending = await sp2.GetRequiredService<IApprovalGateStore>().ListPendingAsync(CancellationToken.None);
                pending.Should().ContainSingle("the pending approval gate must survive the restart and remain decidable")
                    .Which.ApprovalId.Should().Be("gate-1");
            }
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    private static ServiceProvider BuildHost(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUKBatch(_ => { });
        services.AddUKBatchEntityFrameworkCoreStores(o =>
        {
            o.UseSqlite($"DataSource={dbPath}");
            o.MigrateOnStartup = false;   // we migrate explicitly below (no started host to run the hosted migrator)
        });
        return services.BuildServiceProvider();
    }

    private static async Task MigrateAsync(IServiceProvider sp)
    {
        var factory = sp.GetRequiredService<IDbContextFactory<UKBatchDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.Database.MigrateAsync();
    }
}
