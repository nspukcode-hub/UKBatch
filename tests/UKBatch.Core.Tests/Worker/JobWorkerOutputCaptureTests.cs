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
/// Pins the output-capture step in the worker: after a job runs successfully, the values it recorded via
/// <see cref="JobContext.Outputs"/> are persisted to its <see cref="JobExecution.Outputs"/> before the
/// terminal status flip (so a reader that sees Completed already sees the outputs). A job that records
/// nothing leaves <see cref="JobExecution.Outputs"/> null — the unchanged, common case.
/// </summary>
public class JobWorkerOutputCaptureTests
{
    private sealed class EmitsOutputJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.Outputs.Set("orderId", 8264);
            context.Outputs.Set("region", "EU");
            return Task.CompletedTask;
        }
    }

    private sealed class EmitsNoOutputJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<JobExecution> RunToTerminalAsync(IHost host, string jobName)
    {
        var runner = host.Services.GetRequiredService<IJobRunner>();
        var store = host.Services.GetRequiredService<IJobStore>();

        var triggered = await runner.TriggerAsync(jobName, JobParameters.Empty, "tester", CancellationToken.None);

        JobExecution? execution = null;
        var ok = await Waits.ForAsync(async () =>
        {
            execution = await store.GetAsync(triggered.ExecutionId, CancellationToken.None);
            return execution is { Status: JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled };
        }, TimeSpan.FromSeconds(60));
        ok.Should().BeTrue("the execution must reach a terminal status (60s deadlock backstop).");
        return execution!;
    }

    [Fact]
    public async Task JobThatRecordsOutputs_PersistsThemOnTheExecution()
    {
        var host = await TestHostBuilder.StartAsync(b => b.AddJob<EmitsOutputJob>().Named("capture.emits"));
        try
        {
            var execution = await RunToTerminalAsync(host, "capture.emits");

            execution.Status.Should().Be(JobStatus.Completed);
            execution.Outputs.Should().NotBeNull("the recorded outputs are captured on the execution");
            // The in-memory store keeps the boxed CLR values; read through the JSON-aware accessor for parity
            // with persistent stores (where these would be JsonElement).
            var outputs = new JobParameters(execution.Outputs!);
            outputs.GetRequired<int>("orderId").Should().Be(8264);
            outputs.GetRequired<string>("region").Should().Be("EU");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task JobThatRecordsNothing_LeavesOutputsNull()
    {
        var host = await TestHostBuilder.StartAsync(b => b.AddJob<EmitsNoOutputJob>().Named("capture.empty"));
        try
        {
            var execution = await RunToTerminalAsync(host, "capture.empty");

            execution.Status.Should().Be(JobStatus.Completed);
            execution.Outputs.Should().BeNull("a job that writes no output leaves the execution's Outputs unset");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
