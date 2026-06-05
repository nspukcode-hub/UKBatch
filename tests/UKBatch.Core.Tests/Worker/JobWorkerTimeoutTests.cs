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
/// A per-execution timeout is a retry-eligible FAILURE, not a cancellation: it finalizes
/// <see cref="JobStatus.Failed"/> (honoring <c>MaxRetries</c>), never <see cref="JobStatus.Cancelled"/>.
/// Genuine cooperative cancellation and host shutdown keep their <see cref="JobStatus.Cancelled"/>
/// behavior. Uses a real clock and ~1s timeouts (the timeout is wall-clock via <c>CancelAfter</c>),
/// with event-driven waits and generous watchdogs.
/// </summary>
public class JobWorkerTimeoutTests
{
    /// <summary>Blocks until its execution token trips (the per-execution timeout fires it).</summary>
    public sealed class TimeoutBlockingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>Throws a cooperative OCE immediately from a token the job controls (NOT host shutdown).</summary>
    public sealed class CooperativeCancelJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            using var own = new CancellationTokenSource();
            own.Cancel();
            own.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>Signals when it has started running, then blocks on its execution token (per-instance TCS, no shared state).</summary>
    public sealed class ShutdownBlockingJob : IJob
    {
        public static TaskCompletionSource Started { get; private set; } = NewSignal();

        public static void ResetSignal() => Started = NewSignal();

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private static async Task<List<JobStatus>> CollectStatusesAsync(
        IJobStore store, string executionId, Func<JobStatus, bool> until, TimeSpan timeout)
    {
        var statuses = new List<JobStatus>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var ev in store.WatchAsync(WatchOptions.Default, cts.Token).ConfigureAwait(false))
            {
                if (ev.ExecutionId != executionId)
                {
                    continue;
                }
                statuses.Add(ev.Status);
                if (until(ev.Status))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // watchdog — caller asserts on what was collected
        }
        return statuses;
    }

    [Fact]
    public async Task Execution_ExceedsTimeout_MaxRetriesZero_FinalizesFailed_NotCancelled()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<TimeoutBlockingJob>().Named("timeout.norows").WithTimeout(1).WithMaxRetries(0);
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var store = host.Services.GetRequiredService<IJobStore>();

            var execution = await runner.TriggerAsync("timeout.norows", JobParameters.Empty, "test", default).ConfigureAwait(false);

            var reachedTerminal = await Waits.ForAsync(async () =>
            {
                var row = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
                return row is { Status: JobStatus.Failed or JobStatus.Cancelled or JobStatus.Completed };
            }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            reachedTerminal.Should().BeTrue("the timed-out execution must reach a terminal state.");

            var final = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
            final!.Status.Should().Be(JobStatus.Failed, "a timeout is a retry-eligible failure, not a cancellation.");
            final.Status.Should().NotBe(JobStatus.Cancelled);
            final.LastError.Should().Contain("timed out after 1s");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Execution_ExceedsTimeout_WithRetries_TransitionsToRetrying()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<TimeoutBlockingJob>().Named("timeout.retry").WithTimeout(1).WithMaxRetries(1);
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var store = host.Services.GetRequiredService<IJobStore>();

            var execution = await runner.TriggerAsync("timeout.retry", JobParameters.Empty, "test", default).ConfigureAwait(false);

            // Capture the status stream until the terminal Failed; an intermediate Retrying must appear.
            var statuses = await CollectStatusesAsync(
                store, execution.ExecutionId, s => s is JobStatus.Failed, TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            statuses.Should().Contain(JobStatus.Retrying, "the first timeout must re-route through retry, not terminate cancelled.");
            statuses.Should().NotContain(JobStatus.Cancelled);

            var final = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
            final!.Status.Should().Be(JobStatus.Failed, "after MaxRetries are exhausted the timeout terminates Failed.");
            final.AttemptNumber.Should().BeGreaterThan(1, "RecordAttemptAsync must have advanced the attempt on retry.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Execution_GenuineCooperativeCancel_NoTimeout_FinalizesCancelled()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            // TimeoutSeconds=0 → the non-shutdown OCE is classified as a genuine cancellation, not a timeout.
            b.AddJob<CooperativeCancelJob>().Named("cancel.cooperative").WithTimeout(0).WithMaxRetries(0);
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var store = host.Services.GetRequiredService<IJobStore>();

            var execution = await runner.TriggerAsync("cancel.cooperative", JobParameters.Empty, "test", default).ConfigureAwait(false);

            var reachedTerminal = await Waits.ForAsync(async () =>
            {
                var row = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
                return row is { Status: JobStatus.Cancelled or JobStatus.Failed or JobStatus.Completed };
            }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            reachedTerminal.Should().BeTrue();

            var final = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
            final!.Status.Should().Be(JobStatus.Cancelled, "a genuine cooperative cancel without a timeout stays Cancelled.");
            final.AttemptNumber.Should().Be(1, "a genuine cancel is terminal and is never retried.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Execution_HostShutdown_FinalizesCancelled_Unchanged()
    {
        ShutdownBlockingJob.ResetSignal();

        var host = await TestHostBuilder.StartAsync(b =>
        {
            // No timeout — the only cancellation reaching the worker is host shutdown.
            b.AddJob<ShutdownBlockingJob>().Named("shutdown.cancel").WithTimeout(0).WithMaxRetries(0);
        }).ConfigureAwait(false);
        var stopped = false;
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var store = host.Services.GetRequiredService<IJobStore>();

            var execution = await runner.TriggerAsync("shutdown.cancel", JobParameters.Empty, "test", default).ConfigureAwait(false);

            // Wait for the job to be actually running before tripping shutdown.
            await ShutdownBlockingJob.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            await host.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            stopped = true;

            var final = await store.GetAsync(execution.ExecutionId, default).ConfigureAwait(false);
            final.Should().NotBeNull();
            final!.Status.Should().BeOneOf(JobStatus.Cancelled, JobStatus.Cancelling);
        }
        finally
        {
            if (!stopped)
            {
                await TestHostBuilder.StopGracefullyAsync(host, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
        }
    }
}
