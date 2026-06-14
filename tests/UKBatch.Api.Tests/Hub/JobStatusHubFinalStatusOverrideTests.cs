using System.Net.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Api.Hub;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Hub;

/// <summary>
/// The headline proof for the gate-failure colouring fix at the fan-out level: the runtime's terminal
/// verdict, carried on <c>BatchCompletionSignalPayload.RuntimeTerminalStatus</c>, OVERRIDES the
/// row-derived aggregate when emitting <see cref="BatchCompletionSummary.FinalStatus"/>. A rejected /
/// dismissed / timed-out-Fail approval gate ends the batch but leaves NO <see cref="JobExecution"/> row,
/// so without the override an all-Completed row set would report the run green even though it failed.
/// <para>
/// These tests insert controlled rows directly (via the public <see cref="IJobStoreInternal.InsertAsync"/>
/// seam) under a synthetic batch id, then drive the internal completion signal once (by reflection — Core
/// does not grant friend access to this test assembly, matching the existing dedupe test) and assert the
/// emitted summary. The override logic is reduced to its essence: payload verdict vs row aggregate.
/// </para>
/// </summary>
public sealed class JobStatusHubFinalStatusOverrideTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public JobStatusHubFinalStatusOverrideTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private HubConnection BuildHubConnection()
    {
        var baseUri = _factory.Server.BaseAddress;
        var hubUri = new Uri(baseUri, "/api/hubs/jobs");
        return new HubConnectionBuilder()
            .WithUrl(hubUri, opt =>
            {
                opt.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                opt.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    [Theory]
    [InlineData(JobStatus.Failed)]    // gate-dismiss / reject / timeout-Fail essence: rows all green, verdict Failed
    [InlineData(JobStatus.Cancelled)] // host-stop / batch-cancel: rows all green, verdict Cancelled
    public async Task RuntimeTerminalStatus_OverridesAllCompletedRows(JobStatus terminal)
    {
        // Seed three Completed rows under a synthetic batch run id, then signal completion with a
        // non-null RuntimeTerminalStatus. The emitted FinalStatus MUST be the runtime verdict, not Completed.
        var batchId = $"override-{terminal}-{Guid.NewGuid():N}";
        await InsertExecutionRowsAsync(batchId, JobStatus.Completed, JobStatus.Completed, JobStatus.Completed);

        var summary = await TriggerSignalAndAwaitSummaryAsync(batchId, terminal);

        summary.FinalStatus.Should().Be(terminal,
            "a non-null RuntimeTerminalStatus overrides the row aggregate so a gate failure with no row surfaces.");
        // Shard counts stay HONEST job counts (row-derived) even though the roll-up flipped.
        summary.TotalJobs.Should().Be(3);
        summary.SucceededJobs.Should().Be(3);
        summary.FailedJobs.Should().Be(0);
        summary.CancelledJobs.Should().Be(0);
    }

    [Fact]
    public async Task NullRuntimeTerminalStatus_WithOneFailedRow_StillReportsFailed()
    {
        // ContinueOnFailure leaves a Failed row and the runtime does NOT rethrow (verdict null). The row
        // aggregate must still be honoured — no regression from the override change.
        var batchId = $"rows-failed-{Guid.NewGuid():N}";
        await InsertExecutionRowsAsync(batchId, JobStatus.Completed, JobStatus.Failed, JobStatus.Completed);

        var summary = await TriggerSignalAndAwaitSummaryAsync(batchId, runtimeTerminal: null);

        summary.FinalStatus.Should().Be(JobStatus.Failed, "a null verdict trusts the rows, and one row failed.");
        summary.TotalJobs.Should().Be(3);
        summary.FailedJobs.Should().Be(1);
    }

    [Fact]
    public async Task NullRuntimeTerminalStatus_AllCompletedRows_ReportsCompleted()
    {
        // The happy path is unchanged: no verdict, all rows Completed → Completed.
        var batchId = $"rows-ok-{Guid.NewGuid():N}";
        await InsertExecutionRowsAsync(batchId, JobStatus.Completed, JobStatus.Completed);

        var summary = await TriggerSignalAndAwaitSummaryAsync(batchId, runtimeTerminal: null);

        summary.FinalStatus.Should().Be(JobStatus.Completed, "no verdict + all rows green = green.");
        summary.TotalJobs.Should().Be(2);
        summary.SucceededJobs.Should().Be(2);
    }

    // ---- harness ----

    /// <summary>Inserts fully-formed execution rows under <paramref name="batchId"/> via the public InsertAsync seam.</summary>
    private async Task InsertExecutionRowsAsync(string batchId, params JobStatus[] statuses)
    {
        var store = (IJobStoreInternal)_factory.Services.GetRequiredService<IJobStore>();
        var now = DateTimeOffset.UtcNow;
        var i = 0;
        foreach (var status in statuses)
        {
            await store.InsertAsync(
                new JobExecution
                {
                    ExecutionId = $"{batchId}-exec-{i++}",
                    JobName = "OverrideProbeJob",
                    BatchId = batchId,
                    Status = status,
                    Parameters = new Dictionary<string, object?>(),
                    EnqueuedAtUtc = now,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    AttemptNumber = 1,
                    MaxRetries = 0,
                    Processed = 0,
                    Failed = 0,
                },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Subscribes to the hub, drives the internal <c>BatchCompletionSignal</c> ONCE for
    /// <paramref name="batchId"/> with the given verdict, and returns the emitted summary for that batch.
    /// </summary>
    private async Task<BatchCompletionSummary> TriggerSignalAndAwaitSummaryAsync(string batchId, JobStatus? runtimeTerminal)
    {
        await using var connection = BuildHubConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var mySummary = new TaskCompletionSource<BatchCompletionSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            if (s.BatchId == batchId)
            {
                mySummary.TrySetResult(s);
            }
        });
        await connection.StartAsync(cts.Token);
        await connection.InvokeAsync("SubscribeAll", cts.Token);

        SignalCompletion(batchId, runtimeTerminal);

        var winner = await Task.WhenAny(mySummary.Task, Task.Delay(TimeSpan.FromSeconds(40), cts.Token));
        winner.Should().Be(mySummary.Task, "the hub must emit BatchCompleted for the signalled batch within 40s.");
        var summary = await mySummary.Task;
        await connection.StopAsync(cts.Token);
        return summary;
    }

    /// <summary>
    /// Constructs the internal <c>BatchCompletionSignalPayload</c> (incl. RuntimeTerminalStatus) and invokes
    /// <c>BatchCompletionSignal.Signal</c> via reflection — Core grants friend access to UKBatch.Api but
    /// not to this test assembly, so the internal seam is reached reflectively (matching the dedupe test).
    /// </summary>
    private void SignalCompletion(string batchId, JobStatus? runtimeTerminal)
    {
        var coreAssembly = typeof(JobStatusHub).Assembly.GetReferencedAssemblies()
            .Select(System.Reflection.Assembly.Load)
            .First(a => a.GetName().Name == "UKBatch.Core");

        var signalType = coreAssembly.GetType("UKBatch.Runtime.BatchCompletionSignal", throwOnError: false)
            ?? throw new InvalidOperationException("UKBatch.Runtime.BatchCompletionSignal type not found — internal seam relocation?");
        var payloadType = coreAssembly.GetType("UKBatch.Runtime.BatchCompletionSignalPayload", throwOnError: false)
            ?? throw new InvalidOperationException("UKBatch.Runtime.BatchCompletionSignalPayload type not found — internal seam relocation?");

        var signalSvc = _factory.Services.GetService(signalType);
        signalSvc.Should().NotBeNull("BatchCompletionSignal must be registered as a singleton.");

        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("BatchRunId")!.SetValue(payload, batchId);
        payloadType.GetProperty("BatchDefinitionId")!.SetValue(payload, "override-def-id");
        payloadType.GetProperty("BatchName")!.SetValue(payload, "override-batch");
        // The field under test — null vs a runtime verdict.
        payloadType.GetProperty("RuntimeTerminalStatus")!.SetValue(payload, runtimeTerminal);

        var signalMethod = signalType.GetMethod("Signal")!;
        signalMethod.Invoke(signalSvc, new[] { payload });
    }
}
