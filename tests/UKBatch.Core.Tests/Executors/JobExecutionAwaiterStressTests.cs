using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Models;
using UKBatch.Internal;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Executors;

/// <summary>
/// Verifies JobExecutionAwaiter scales to N=11000 concurrent waiters (single WatchAsync
/// subscription, no event drift, bounded heap growth).
/// </summary>
[Trait("Category", "Stress")]
public class JobExecutionAwaiterStressTests
{
    [Fact]
    public async Task ElevenThousandConcurrentWaiters_AllCompleteUnderHeapBudget()
    {
        const int N = 11_000;
        var store = new InMemoryJobStore(TimeProvider.System, Options.Create(new UKBatchOptions { WatchBufferCapacity = 65536 }), new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
        var awaiter = new JobExecutionAwaiter(store, NullLogger<JobExecutionAwaiter>.Instance);
        // StartAsync registers the watch subscription synchronously before returning, so no warmup
        // probe is needed — waiters registered immediately afterwards are guaranteed to observe the
        // events that follow.
        await awaiter.StartAsync(default).ConfigureAwait(false);

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

        try
        {
            var pairs = new List<(string id, Task<JobExecution> wait)>(N);
            for (var i = 0; i < N; i++)
            {
                var id = IdGenerator.NewExecutionId();
                pairs.Add((id, awaiter.WaitForTerminalAsync(id, default)));
            }

            // Trip every one to terminal in parallel.
            await Task.WhenAll(pairs.Select(p => Task.Run(async () =>
            {
                await store.InsertAsync(NewExecution(p.id), default).ConfigureAwait(false);
                await store.UpdateStatusAsync(p.id, JobStatus.Running, null, default).ConfigureAwait(false);
                await store.UpdateStatusAsync(p.id, JobStatus.Completed, null, default).ConfigureAwait(false);
            }))).ConfigureAwait(false);

            // Progress-aware wait — a fixed wall-clock budget flakes on slow CI runners. As long as
            // waiters keep resolving, keep waiting; thirty seconds with ZERO progress is the actual
            // deadlock signature this test exists to catch. Arming and elapsed measurement share the
            // same clock source on purpose.
            var waits = pairs.Select(p => p.wait).ToList();
            var lastCompleted = 0;
            var stallStartedAt = Environment.TickCount64;
            while (!Task.WhenAll(waits).IsCompleted)
            {
                var completed = waits.Count(w => w.IsCompleted);
                if (completed > lastCompleted)
                {
                    lastCompleted = completed;
                    stallStartedAt = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - stallStartedAt > 30_000)
                {
                    throw new TimeoutException(
                        $"Waiter resolution stalled: {completed}/{N} completed with no progress for 30s.");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            await Task.WhenAll(waits).ConfigureAwait(false);

            // Every waiter must have completed.
            pairs.All(p => p.wait.IsCompletedSuccessfully).Should().BeTrue();

            // Bounded heap growth: per-waiter overhead must stay small. Forces a GC and measures.
            var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
            var delta = heapAfter - heapBefore;
            delta.Should().BeLessThan(150L * 1024 * 1024, $"heap delta {delta:N0} bytes; budget < 150MB allowing CI headroom");
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JobExecution NewExecution(string id) => new()
    {
        ExecutionId = id,
        JobName = "j",
        Status = JobStatus.Pending,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
        MaxRetries = 0,
        Processed = 0,
        Failed = 0,
    };
}
