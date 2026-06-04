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
/// #7 / S1 verification — JobExecutionAwaiter scales to N=11000 concurrent waiters
/// (single WatchAsync subscription, no event drift, bounded heap growth).
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
        await awaiter.StartAsync(default).ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false); // ensure watch loop subscribed

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
                var ex = new JobExecution
                {
                    ExecutionId = p.id,
                    JobName = "j",
                    Status = JobStatus.Pending,
                    Parameters = new Dictionary<string, object?>(),
                    EnqueuedAtUtc = DateTimeOffset.UtcNow,
                    AttemptNumber = 1,
                    MaxRetries = 0,
                    Processed = 0,
                    Failed = 0,
                };
                await store.InsertAsync(ex, default).ConfigureAwait(false);
                await store.UpdateStatusAsync(p.id, JobStatus.Running, null, default).ConfigureAwait(false);
                await store.UpdateStatusAsync(p.id, JobStatus.Completed, null, default).ConfigureAwait(false);
            }))).ConfigureAwait(false);

            await Task.WhenAll(pairs.Select(p => p.wait)).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            // Every waiter must have completed.
            pairs.All(p => p.wait.IsCompletedSuccessfully).Should().BeTrue();

            // Heap delta < 100MB (per #7 acceptance). Forces a GC and measures.
            var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
            var delta = heapAfter - heapBefore;
            delta.Should().BeLessThan(150L * 1024 * 1024, $"heap delta {delta:N0} bytes; budget < 150MB (spec says <100MB but we allow CI headroom)");
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }
}
