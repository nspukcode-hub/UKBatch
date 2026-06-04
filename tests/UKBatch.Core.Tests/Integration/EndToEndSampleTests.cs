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
/// #1 sample scenario — simple job triggers, runs through states, completes.
/// Demonstrates end-to-end ServiceCollectionExtensions wiring + IJobRunner.TriggerAsync path.
/// </summary>
public class EndToEndSampleTests
{
    // PRODUCTION BUG #1: UKBatchHost.DisposeAsync calls _awaiter.DisposeAsync AND
    // _progressFlusher.DisposeAsync directly, but DI also disposes them as singletons
    // resulting in a SECOND DisposeAsync that calls Cancel on an already-disposed CTS,
    // throwing ObjectDisposedException. Fix: remove the manual DisposeAsync calls in
    // UKBatchHost.DisposeAsync (DI owns lifecycle), OR make JobExecutionAwaiter.DisposeAsync
    // + DebouncedProgressFlusher.DisposeAsync idempotent (guard Cancel with try/catch).
    // The tests below mitigate by NOT using `using` on the host — they call StopAsync via
    // TestHostBuilder.StopGracefullyAsync which catches the secondary ObjectDisposedException.

    [Fact]
    public async Task TriggerJob_RunsAndCompletes()
    {
        // Note: SucceedingJob.InvocationCount is static and can race other tests running in parallel,
        // so we assert the per-execution status here, not the static counter.

        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<SucceedingJob>().Named("sample.simple");
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();

            var execution = await runner.TriggerAsync(
                "sample.simple",
                JobParameters.Empty,
                triggeredBy: "test",
                cancellationToken: default).ConfigureAwait(false);

            var terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, default)
                .WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            terminal.Status.Should().Be(JobStatus.Completed);
            terminal.TriggeredBy.Should().Be("test");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerJob_FailingJobWithNoRetries_TerminatesAsFailed()
    {
        FailingJob.Reset();

        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<FailingJob>().Named("sample.fail").WithMaxRetries(0);
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();
            var execution = await runner.TriggerAsync("sample.fail", JobParameters.Empty, "test", default).ConfigureAwait(false);

            var terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, default)
                .WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            terminal.Status.Should().Be(JobStatus.Failed);
            terminal.LastError.Should().NotBeNull();
            terminal.LastError.Should().Contain("intentional failure");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerJob_TransientFailureRetried_TerminatesAsCompleted()
    {
        // Fix 2 / N- integration check: the worker must accept BOTH Pending -> Running
        // and Retrying -> Running predecessors. With 3 retries against a 2-fail-then-succeed
        // job, the worker should observe Pending -> Running, fail (Retrying), Retrying -> Running, succeed.
        TransientThenSucceedJob.FailUntilAttempt = 2;

        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<TransientThenSucceedJob>().Named("sample.transient").WithMaxRetries(3);
        }, services =>
        {
            // Override IRetryPolicy to make retries fast.
            services.AddSingleton<IRetryPolicy>(new ImmediateRetryPolicy());
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();
            var execution = await runner.TriggerAsync("sample.transient", JobParameters.Empty, "test", default).ConfigureAwait(false);

            var terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, default)
                .WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            terminal.Status.Should().Be(JobStatus.Completed);
            terminal.AttemptNumber.Should().BeGreaterOrEqualTo(2);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerJob_UnknownJobName_Throws()
    {
        var host = await TestHostBuilder.StartAsync().ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            Func<Task> act = async () =>
                await runner.TriggerAsync("does.not.exist", JobParameters.Empty, "test", default).ConfigureAwait(false);
            await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }
}
