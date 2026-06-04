using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Models;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// Concurrent CRUD stress: promotion — AddOrUpdate handles concurrency with no spin loops.
/// </summary>
public class InMemoryJobStoreConcurrencyTests
{
    [Fact]
    public async Task UpdateProgressAsync_1000ConcurrentWrites_AllSucceed()
    {
        var store = new InMemoryJobStore(TimeProvider.System, Options.Create(new UKBatchOptions()), new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
        var def = new JobDefinition
        {
            Name = "j",
            IsPartitioned = false,
            MaxRetries = 0,
            TimeoutSeconds = 0,
            DefaultParameters = new Dictionary<string, object?>(),
            Tags = Array.Empty<string>(),
        };
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);

        const int N = 1000;
        await Task.WhenAll(Enumerable.Range(0, N).Select(i => Task.Run(async () =>
            await store.UpdateProgressAsync(execution.ExecutionId, i, 0, null, default).ConfigureAwait(false)))).ConfigureAwait(false);

        var final = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
        final.Should().NotBeNull();
        final!.Processed.Should().BeInRange(0, N - 1); // last writer wins; pick any valid value
    }

    [Fact]
    public async Task CreateAsync_1000Concurrent_NoCollisionsAndAllInserted()
    {
        var store = new InMemoryJobStore(TimeProvider.System, Options.Create(new UKBatchOptions()), new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
        var def = new JobDefinition
        {
            Name = "j",
            IsPartitioned = false,
            MaxRetries = 0,
            TimeoutSeconds = 0,
            DefaultParameters = new Dictionary<string, object?>(),
            Tags = Array.Empty<string>(),
        };
        const int N = 1000;
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => Task.Run(async () =>
        {
            var ex = await store.CreateAsync(def, default).ConfigureAwait(false);
            ids.Add(ex.ExecutionId);
        }))).ConfigureAwait(false);

        ids.Should().HaveCount(N);
        ids.Distinct().Should().HaveCount(N);
        var count = await store.CountAsync(new JobQuery { Limit = N }, default).ConfigureAwait(false);
        count.Should().Be(N);
    }

    [Fact]
    public async Task UpdateStatusAsync_RaceFromPendingToRunning_OnlyOneSucceeds()
    {
        var store = new InMemoryJobStore(TimeProvider.System, Options.Create(new UKBatchOptions()), new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
        var def = new JobDefinition
        {
            Name = "j",
            IsPartitioned = false,
            MaxRetries = 0,
            TimeoutSeconds = 0,
            DefaultParameters = new Dictionary<string, object?>(),
            Tags = Array.Empty<string>(),
        };
        var execution = await store.CreateAsync(def, default).ConfigureAwait(false);
        // Move to Running.
        await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Running, null, default).ConfigureAwait(false);

        // Two concurrent transitions: Running -> Completed (legal), Running -> Failed (legal).
        // Both are individually legal but the SECOND one will see a terminal state and fail
        // because the matrix forbids Completed -> Failed (or Failed -> Completed). Exactly one
        // of the two transitions should win.
        var t1 = Task.Run(async () =>
        {
            try { await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Completed, null, default).ConfigureAwait(false); return true; }
            catch { return false; }
        });
        var t2 = Task.Run(async () =>
        {
            try { await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Failed, "race", default).ConfigureAwait(false); return true; }
            catch { return false; }
        });

        var results = await Task.WhenAll(t1, t2).ConfigureAwait(false);
        // Exactly one should succeed and the other should be rejected by the state machine.
        results.Count(r => r).Should().Be(1);
    }
}
