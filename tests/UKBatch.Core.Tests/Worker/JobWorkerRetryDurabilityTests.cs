using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Worker;

/// <summary>
/// acceptance / verification — 1000 retries during graceful shutdown leaves NO row
/// stuck in <see cref="JobStatus.Running"/>.
/// </summary>
[Trait("Category", "Stress")]
public class JobWorkerRetryDurabilityTests
{
    /// <summary>A job that always throws — every attempt fails.</summary>
    public sealed class AlwaysFailJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"always fail attempt {context.AttemptNumber}");
    }

    [Fact]
    public async Task ThousandRetriesDuringShutdown_NoRowStuckInRunning()
    {
        // invariant: status committed before Task.Delay so a host crash mid-delay leaves a
        // recoverable Retrying row.
        const int N = 1000;

        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<AlwaysFailJob>().Named("retrying.fail").WithMaxRetries(10);
            b.Configure(opts =>
            {
                opts.MaxDegreeOfParallelism = 4;
                opts.DispatcherChannelCapacity = 2048;
                opts.ShutdownTimeout = TimeSpan.FromSeconds(15);
            });
        }, services =>
        {
            // Fixed delay long enough that many jobs will be sitting in Retrying when shutdown fires.
            services.AddSingleton<IRetryPolicy>(new FixedDelayRetryPolicy(TimeSpan.FromMilliseconds(200)));
        }).ConfigureAwait(false);

        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var store = host.Services.GetRequiredService<IJobStore>();
            var execIds = new List<string>(N);
            for (var i = 0; i < N; i++)
            {
                var ex = await runner.TriggerAsync("retrying.fail", JobParameters.Empty, "test", default).ConfigureAwait(false);
                execIds.Add(ex.ExecutionId);
            }

            // Let dispatching kick in.
            await Task.Delay(200).ConfigureAwait(false);

            // Initiate shutdown mid-stream.
            await host.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            // After shutdown completes, snapshot every row.
            var rows = new List<JobExecution>();
            foreach (var id in execIds)
            {
                var r = await store.GetAsync(id, default).ConfigureAwait(false);
                r.Should().NotBeNull();
                rows.Add(r!);
            }

            // invariant: NO row should be in Running.
            rows.Where(r => r.Status == JobStatus.Running)
                .Should().BeEmpty("B3 invariant — no row may be stuck in Running after shutdown");

            // Every row should be in one of: Retrying, Cancelled, Failed, Completed (or Pending if never picked up).
            // (Cancelling is also possible if the shutdown timeout fired mid-transition.)
            var validPostShutdown = new[]
            {
                JobStatus.Retrying,
                JobStatus.Cancelled,
                JobStatus.Cancelling,
                JobStatus.Failed,
                JobStatus.Pending,
                JobStatus.Completed,
            };
            rows.Should().AllSatisfy(r =>
                validPostShutdown.Should().Contain(r.Status, "row must end in a valid post-shutdown state, not Running"));
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }
}
