using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Integration;

/// <summary>
/// #2 acceptance — 1000 concurrent triggers with mixed outcomes. No deadlocks, no leaks.
/// </summary>
[Trait("Category", "Stress")]
public class ConcurrentTriggersTests
{
    [Fact]
    public async Task ThousandConcurrentTriggers_MixedOutcomes_AllTerminalNoDeadlocks()
    {
        SucceedingJob.Reset();
        FailingJob.Reset();
        TransientThenSucceedJob.Reset();
        TransientThenSucceedJob.FailUntilAttempt = 2;

        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<SucceedingJob>().Named("ok").WithMaxRetries(0);
            b.AddJob<FailingJob>().Named("fail").WithMaxRetries(0);
            b.AddJob<TransientThenSucceedJob>().Named("transient").WithMaxRetries(2);

            b.Configure(opts =>
            {
                opts.MaxDegreeOfParallelism = 8;
                opts.DispatcherChannelCapacity = 1024;
                opts.ShutdownTimeout = TimeSpan.FromSeconds(20);
            });
        }, services =>
        {
            services.AddSingleton<IRetryPolicy>(new ImmediateRetryPolicy());
        }).ConfigureAwait(false);

        try
        {
            const int Total = 1000;
            const int Succeed = 500;
            const int Fail = 250;
            const int Transient = 250;

            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();
            var execIds = new List<string>();
            var tasks = new List<Task<JobExecution>>();

            for (var i = 0; i < Succeed; i++)
            {
                tasks.Add(runner.TriggerAsync("ok", JobParameters.Empty, "test", default));
            }
            for (var i = 0; i < Fail; i++)
            {
                tasks.Add(runner.TriggerAsync("fail", JobParameters.Empty, "test", default));
            }
            for (var i = 0; i < Transient; i++)
            {
                tasks.Add(runner.TriggerAsync("transient", JobParameters.Empty, "test", default));
            }

            var executions = await Task.WhenAll(tasks).ConfigureAwait(false);
            execIds.AddRange(executions.Select(e => e.ExecutionId));

            // Wait for every one to reach terminal state — progress-aware, NOT a fixed wall-clock
            // budget. A fixed timeout measures machine speed, not correctness: on a loaded runner
            // (full-suite parallel load, 2-core CI) 1000 executions legitimately take minutes. The
            // assertion target is "no deadlock", and a deadlock looks like NOTHING terminating for a
            // sustained window — so we keep waiting while work keeps retiring and fail only when no
            // execution completes for a full stall window.
            var waitTasks = execIds.Select(id => awaiter.WaitForTerminalAsync(id, default)).ToArray();
            var allDone = Task.WhenAll(waitTasks);
            var stallWindow = TimeSpan.FromSeconds(30);
            var lastProgress = waitTasks.Count(t => t.IsCompleted);
            var lastProgressAt = Environment.TickCount64;
            while (!allDone.IsCompleted)
            {
                await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
                var done = waitTasks.Count(t => t.IsCompleted);
                if (done > lastProgress)
                {
                    lastProgress = done;
                    lastProgressAt = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - lastProgressAt > stallWindow.TotalMilliseconds)
                {
                    throw new TimeoutException(
                        $"No execution reached a terminal state for {stallWindow.TotalSeconds}s " +
                        $"({done}/{Total} done) — possible dispatcher deadlock.");
                }
            }
            await allDone.ConfigureAwait(false); // propagate any awaiter faults

            // Tally outcomes.
            var store = host.Services.GetRequiredService<IJobStore>();
            var all = new List<JobExecution>();
            foreach (var id in execIds)
            {
                var e = await store.GetAsync(id, default).ConfigureAwait(false);
                e.Should().NotBeNull();
                all.Add(e!);
            }

            // Every execution must be in a terminal state.
            all.Should().AllSatisfy(e =>
                BatchStateMachine.IsTerminal(e.Status).Should().BeTrue(
                    $"all triggered executions must terminate (got {e.Status} for {e.ExecutionId})"));

            var completedCount = all.Count(e => e.Status == JobStatus.Completed);
            var failedCount = all.Count(e => e.Status == JobStatus.Failed);

            completedCount.Should().BeGreaterOrEqualTo(Succeed, "succeed-only batch should all succeed");
            failedCount.Should().BeGreaterOrEqualTo(Fail, "fail-only batch should all fail");

            // Total counts add to Total (no row created without being awaited).
            (completedCount + failedCount + all.Count(e => e.Status == JobStatus.Cancelled)).Should().Be(Total);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
    }
}
