using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// Durable-resume forwarding survives a host restart over a SQLite <b>file</b>, Docker-free. A step's
/// captured <see cref="JobExecution.Outputs"/> and the run's <see cref="BatchRun.ForwardedState"/> are both
/// written through the production stores on a first container, which is then disposed ("process dies"); a
/// cold-boot second container over the SAME file reads both back. This is the storage-level proof underneath
/// the end-to-end resume: the forwarded values a resume needs are genuinely on disk.
/// </summary>
public sealed class StepOutputForwardingRestartTests
{
    [Fact]
    public async Task RestartOverSameSqliteFile_ExecutionOutputs_AndRunForwardedState_Survive()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ukbatch-fwd-restart-{Guid.NewGuid():N}.db");
        try
        {
            // ===== Host 1: capture an execution's outputs and a run's forwarded state, then "crash". =====
            await using (var sp1 = BuildHost(dbPath))
            {
                await MigrateAsync(sp1);

                await sp1.GetRequiredService<IJobStoreInternal>().InsertAsync(
                    TestData.Execution("exec-1", batchId: "run-1", status: JobStatus.Running),
                    CancellationToken.None);
                await sp1.GetRequiredService<IJobExecutionWriter>().UpdateOutputsAsync(
                    "exec-1",
                    new Dictionary<string, object?> { ["orderId"] = 5, ["region"] = "EU" },
                    CancellationToken.None);

                await sp1.GetRequiredService<IBatchRunStore>().CreateAsync(
                    TestData.BatchRun("run-1", stepCount: 2), CancellationToken.None);
                await sp1.GetRequiredService<IBatchRunStore>().UpdateForwardedStateAsync(
                    "run-1",
                    new Dictionary<string, object?>
                    {
                        ["ukbatch.initialParameters"] = new Dictionary<string, object?> { ["tenant"] = "acme" },
                        ["ukbatch.forwardedOutputs"] = new Dictionary<string, object?> { ["orderId"] = 5 },
                    },
                    CancellationToken.None);
            }

            // ===== Host 2: cold boot over the SAME file reads outputs + forwarded state back. =====
            await using (var sp2 = BuildHost(dbPath))
            {
                await MigrateAsync(sp2);   // idempotent on the existing file

                var execution = await sp2.GetRequiredService<IJobStoreInternal>().GetAsync("exec-1", CancellationToken.None);
                execution.Should().NotBeNull("the execution must survive the restart");
                execution!.Outputs.Should().NotBeNull("captured outputs must survive the restart");
                // JSON-aware read recovers the typed values (JsonElement after the round-trip).
                var outputs = new JobParameters(execution.Outputs!);
                outputs.GetRequired<int>("orderId").Should().Be(5);
                outputs.GetRequired<string>("region").Should().Be("EU");

                var run = await sp2.GetRequiredService<IBatchRunStore>().GetAsync("run-1", CancellationToken.None);
                run.Should().NotBeNull("the run must survive the restart");
                run!.ForwardedState.Should().NotBeNull("the forwarded state a resume needs must survive the restart");
                run.ForwardedState!.Should().ContainKey("ukbatch.initialParameters");
                run.ForwardedState!.Should().ContainKey("ukbatch.forwardedOutputs");
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
            o.MigrateOnStartup = false;   // migrated explicitly below (no started host to run the hosted migrator)
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
