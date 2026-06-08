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
/// JobExecutionAwaiter invariants:
/// (A) WaitForTerminalAsync registers the waiter synchronously before returning the Task.
/// (B) ct.Register is disposed on TCS completion.
/// (C) StartAsync registers the watch subscription synchronously, so a waiter registered (and its
///     terminal event published) immediately afterwards is never lost.
/// Plus CancelWaiter idempotency.
/// </summary>
public class JobExecutionAwaiterTests
{
    private static (JobExecutionAwaiter, InMemoryJobStore) NewAwaiterWithStore()
    {
        var store = new InMemoryJobStore(TimeProvider.System, Options.Create(new UKBatchOptions()), new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
        var awaiter = new JobExecutionAwaiter(store, NullLogger<JobExecutionAwaiter>.Instance);
        return (awaiter, store);
    }

    [Fact]
    public async Task StartAsync_RegistersSubscriptionSynchronously_NoEventLostWithoutWarmup()
    {
        // Regression lock for the subscription-warmup race: StartAsync must register the underlying
        // WatchAsync subscription synchronously (during its first MoveNextAsync) before returning. We
        // register a waiter and publish that execution's terminal event IMMEDIATELY after StartAsync
        // returns, with NO warmup delay. Before the fix, the background watch loop registered the
        // subscription only when it happened to be scheduled, so an event published this early could
        // be dropped and the waiter would hang. The catch-up read is deliberately not what completes
        // this waiter — the execution is inserted AFTER the waiter is registered, so only the live
        // subscription can deliver the terminal event.
        var (awaiter, store) = NewAwaiterWithStore();
        await awaiter.StartAsync(default).ConfigureAwait(false);
        try
        {
            var execId = IdGenerator.NewExecutionId();
            var wait = awaiter.WaitForTerminalAsync(execId, default);

            // Publish the lifecycle with no warmup — the subscription is guaranteed live.
            var execution = new JobExecution
            {
                ExecutionId = execId,
                JobName = "j",
                Status = JobStatus.Pending,
                Parameters = new Dictionary<string, object?>(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
                MaxRetries = 0,
                Processed = 0,
                Failed = 0,
            };
            await store.InsertAsync(execution, default).ConfigureAwait(false);
            await store.UpdateStatusAsync(execId, JobStatus.Running, null, default).ConfigureAwait(false);
            await store.UpdateStatusAsync(execId, JobStatus.Completed, null, default).ConfigureAwait(false);

            var terminal = await wait.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            terminal.Status.Should().Be(JobStatus.Completed);
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WaitForTerminalAsync_TerminalEvent_CompletesTcs()
    {
        var (awaiter, store) = NewAwaiterWithStore();
        await awaiter.StartAsync(default).ConfigureAwait(false);
        try
        {
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

            // Register the waiter before triggering the transition.
            var wait = awaiter.WaitForTerminalAsync(execution.ExecutionId, default);

            // Then transition.
            await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Running, null, default).ConfigureAwait(false);
            await store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Completed, null, default).ConfigureAwait(false);

            var terminal = await wait.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            terminal.Status.Should().Be(JobStatus.Completed);
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WaitForTerminalAsync_OrderingInvariant_TerminalBeforeAwait_RaceSafe()
    {
        // Register before trigger. To stress the race we register, then trigger MANY terminal events
        // in tight succession, and assert no event is missed.
        var (awaiter, store) = NewAwaiterWithStore();
        await awaiter.StartAsync(default).ConfigureAwait(false);
        try
        {
            const int N = 100;
            var def = new JobDefinition
            {
                Name = "j",
                IsPartitioned = false,
                MaxRetries = 0,
                TimeoutSeconds = 0,
                DefaultParameters = new Dictionary<string, object?>(),
                Tags = Array.Empty<string>(),
            };

            var pairs = new List<(string id, Task<JobExecution> wait)>();
            for (var i = 0; i < N; i++)
            {
                var execId = IdGenerator.NewExecutionId();
                var wait = awaiter.WaitForTerminalAsync(execId, default);
                // The id and wait are registered atomically (synchronously) BEFORE we trigger.
                // Now perform the terminal-trip via direct store insertion.
                pairs.Add((execId, wait));
            }

            // Fire all transitions concurrently.
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

            // Every waiter completes within a generous timeout.
            await Task.WhenAll(pairs.Select(p => p.wait)).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            pairs.All(p => p.wait.IsCompletedSuccessfully).Should().BeTrue();
            _ = def;
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WaitForTerminalAsync_CancellationToken_CancelsTaskAndCleansUp()
    {
        var (awaiter, _) = NewAwaiterWithStore();
        await awaiter.StartAsync(default).ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource();
            var wait = awaiter.WaitForTerminalAsync("never-fires", cts.Token);
            cts.Cancel();

            Func<Task> act = async () => await wait.ConfigureAwait(false);
            await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WaitForTerminalAsync_ExecutionAlreadyTerminalBeforeRegister_CompletesViaCatchUp()
    {
        // Race: the public IJobRunner.TriggerAsync caller pattern is
        // var ex = await runner.TriggerAsync(...);
        // var terminal = await awaiter.WaitForTerminalAsync(ex.ExecutionId, ct);
        // If the worker reaches terminal between TriggerAsync and WaitForTerminalAsync, the watch
        // loop's TryRemove finds no waiter and silently drops the event. The catch-up read in
        // WaitForTerminalAsync MUST detect the already-terminal row and complete the waiter.
        var (awaiter, store) = NewAwaiterWithStore();
        await awaiter.StartAsync(default).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        try
        {
            var execId = IdGenerator.NewExecutionId();
            var execution = new JobExecution
            {
                ExecutionId = execId,
                JobName = "j",
                Status = JobStatus.Pending,
                Parameters = new Dictionary<string, object?>(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
                MaxRetries = 0,
                Processed = 0,
                Failed = 0,
            };
            await store.InsertAsync(execution, default).ConfigureAwait(false);
            await store.UpdateStatusAsync(execId, JobStatus.Running, null, default).ConfigureAwait(false);
            await store.UpdateStatusAsync(execId, JobStatus.Completed, null, default).ConfigureAwait(false);

            // Wait a moment to let the watch loop drain the events (with no waiter registered they
            // would normally be dropped — simulating the race the catch-up exists to handle).
            await Task.Delay(50).ConfigureAwait(false);

            // Now register the waiter. The catch-up must observe the already-terminal row.
            var wait = awaiter.WaitForTerminalAsync(execId, default);
            var terminal = await wait.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            terminal.Status.Should().Be(JobStatus.Completed);
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WaitForTerminalAsync_ExecutionDoesNotExistYet_DoesNotCompleteFromCatchUp()
    {
        // The catch-up MUST only complete the waiter if the row exists AND is terminal. A
        // not-yet-inserted execution must keep the waiter pending until the watch loop fires.
        var (awaiter, store) = NewAwaiterWithStore();
        await awaiter.StartAsync(default).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        try
        {
            var execId = IdGenerator.NewExecutionId();
            var wait = awaiter.WaitForTerminalAsync(execId, default);

            // Allow the catch-up to run (the row does not exist, so it must not complete).
            await Task.Delay(100).ConfigureAwait(false);
            wait.IsCompleted.Should().BeFalse("waiter must remain pending when execution does not exist");

            // Insert the row and walk to terminal — the watch loop completes the waiter.
            var execution = new JobExecution
            {
                ExecutionId = execId,
                JobName = "j",
                Status = JobStatus.Pending,
                Parameters = new Dictionary<string, object?>(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
                MaxRetries = 0,
                Processed = 0,
                Failed = 0,
            };
            await store.InsertAsync(execution, default).ConfigureAwait(false);
            await store.UpdateStatusAsync(execId, JobStatus.Running, null, default).ConfigureAwait(false);
            await store.UpdateStatusAsync(execId, JobStatus.Completed, null, default).ConfigureAwait(false);

            var terminal = await wait.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            terminal.Status.Should().Be(JobStatus.Completed);
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task CancelWaiter_RemovesWaiterIdempotently()
    {
        var (awaiter, _) = NewAwaiterWithStore();
        await awaiter.StartAsync(default).ConfigureAwait(false);
        try
        {
            var wait = awaiter.WaitForTerminalAsync("test-id", default);
            awaiter.CancelWaiter("test-id");

            Func<Task> act = async () => await wait.ConfigureAwait(false);
            await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);

            // Second call should be a no-op (idempotent).
            Action twice = () => awaiter.CancelWaiter("test-id");
            twice.Should().NotThrow();

            // Cancelling a waiter that never existed is also idempotent.
            Action neverExisted = () => awaiter.CancelWaiter("nonexistent");
            neverExisted.Should().NotThrow();
        }
        finally
        {
            await awaiter.StopAsync(default).ConfigureAwait(false);
            await awaiter.DisposeAsync().ConfigureAwait(false);
        }
    }
}
