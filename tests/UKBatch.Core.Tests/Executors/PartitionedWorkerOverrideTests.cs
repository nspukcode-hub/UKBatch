using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Executors;

/// <summary>
/// Per-run partition worker-count override via the reserved <c>"ukbatch.workers"</c> job
/// parameter (<c>PartitionedJobRunner.WorkerCountParameterKey</c>). The probe job measures the PEAK
/// concurrent <c>ProcessAsync</c> count, which IS the effective worker count once the bounded channel
/// saturates (items ≫ workers, per-item delay ≫ scheduling jitter).
/// </summary>
public class PartitionedWorkerOverrideTests
{
    public sealed class ConcurrencyProbeJob : IPartitionedJob<int>
    {
        public const int Items = 24;
        private static int _current;
        public static int MaxObserved;

        public static void Reset()
        {
            Interlocked.Exchange(ref _current, 0);
            Interlocked.Exchange(ref MaxObserved, 0);
        }

        public async IAsyncEnumerable<int> SourceAsync(
            JobContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < Items; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task ProcessAsync(int item, JobContext context, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _current);
            // CAS-update the high-water mark.
            int seen;
            while (now > (seen = Volatile.Read(ref MaxObserved)))
            {
                Interlocked.CompareExchange(ref MaxObserved, now, seen);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
            Interlocked.Decrement(ref _current);
        }
    }

    private static async Task<(JobStatus Status, int MaxConcurrent)> RunAsync(
        int registeredWorkers, object? overrideValue)
    {
        ConcurrencyProbeJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddPartitionedJob<ConcurrencyProbeJob, int>().Named("probe.workers")
                .WithParallelism(registeredWorkers)
                .WithItemErrorPolicy(ItemErrorPolicy.FailFast)
                .WithMaxRetries(0);
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();
            var parameters = overrideValue is null
                ? JobParameters.Empty
                : new JobParameters(new Dictionary<string, object?> { ["ukbatch.workers"] = overrideValue });

            var execution = await runner.TriggerAsync("probe.workers", parameters, "test", default).ConfigureAwait(false);
            var terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, default)
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return (terminal.Status, ConcurrencyProbeJob.MaxObserved);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Override_IntParameter_LimitsConcurrencyBelowRegistration()
    {
        // Registered 8 workers, run asks for 2 → the peak MUST be exactly 2 (24 items saturate it).
        var (status, max) = await RunAsync(registeredWorkers: 8, overrideValue: 2).ConfigureAwait(false);
        status.Should().Be(JobStatus.Completed);
        max.Should().Be(2);
    }

    [Fact]
    public async Task Override_StringParameter_ParsesAndRaisesAboveRegistration()
    {
        // Registered 1 worker, run asks for "3" (the JsonElement-over-broker shape is stringly too).
        var (status, max) = await RunAsync(registeredWorkers: 1, overrideValue: "3").ConfigureAwait(false);
        status.Should().Be(JobStatus.Completed);
        max.Should().Be(3);
    }

    [Fact]
    public async Task Override_Garbage_FallsBackToRegisteredCount()
    {
        var (status, max) = await RunAsync(registeredWorkers: 2, overrideValue: "not-a-number").ConfigureAwait(false);
        status.Should().Be(JobStatus.Completed);
        max.Should().Be(2);
    }

    [Fact]
    public async Task Override_NonPositive_FallsBackToRegisteredCount()
    {
        var (status, max) = await RunAsync(registeredWorkers: 2, overrideValue: 0).ConfigureAwait(false);
        status.Should().Be(JobStatus.Completed);
        max.Should().Be(2);
    }

    [Fact]
    public async Task Override_AboveClamp_IsClampedNotRejected()
    {
        // 500 → clamped to 128 (NOT a fallback to the registered 1): with 24 items the peak rises
        // well above the registered single worker, proving the override took effect.
        var (status, max) = await RunAsync(registeredWorkers: 1, overrideValue: 500).ConfigureAwait(false);
        status.Should().Be(JobStatus.Completed);
        max.Should().BeGreaterThan(4);
    }
}
