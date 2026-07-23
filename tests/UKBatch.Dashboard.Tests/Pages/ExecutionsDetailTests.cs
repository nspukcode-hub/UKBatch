using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Components.Pages.Executions;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

public sealed class ExecutionsDetailTests : TestContext
{
    public ExecutionsDetailTests() => this.AddPermitAllAuth();

    private static JobExecution Snapshot(string id, JobStatus status) => new()
    {
        ExecutionId = id,
        JobName = "test-job",
        Status = status,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
        AttemptNumber = 1,
        MaxRetries = 3,
        Processed = 0,
        Failed = 0,
    };

    [Fact]
    public async Task Init_SubscribesBeforeFetch_CanonicalOrder()
    {
        // invariant: SubscribeToExecutionAsync MUST be called BEFORE GetExecutionAsync.
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();

        var order = new List<string>();
        client.SubscribeToExecutionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { order.Add("subscribe"); return Task.CompletedTask; });
        client.GetExecutionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { order.Add("fetch"); return Task.FromResult<JobExecution?>(Snapshot("execA", JobStatus.Running)); });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, "execA"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("test-job"));
        order.Should().StartWith("subscribe", because: "R4 — subscribe-first-then-fetch invariant");
        order.Should().Contain("fetch");
        order.IndexOf("subscribe").Should().BeLessThan(order.IndexOf("fetch"));
    }

    [Fact]
    public void Render_Snapshot_ShowsStatusBadgeAndProgress()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        var exec = Snapshot("execA", JobStatus.Running) with { Processed = 50, Failed = 5, Total = 100 };
        client.GetExecutionAsync("execA", Arg.Any<CancellationToken>()).Returns(Task.FromResult<JobExecution?>(exec));

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, "execA"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("RUNNING"));
        cut.Markup.Should().Contain("progress-bar");
    }

    [Fact]
    public void Render_Snapshot_ShowsFullCopyableId()
    {
        // The page title abbreviates the id; the snapshot card must carry the FULL id (via CopyableId)
        // so the operator can copy it into the Executions exact-match filter. The id is a long UUIDv7 to
        // make abbreviation observable.
        const string fullId = "0192a9c1-7b3e-7def-bc01-execdetailid1";
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.GetExecutionAsync(fullId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JobExecution?>(Snapshot(fullId, JobStatus.Running)));

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, fullId));

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<UKBatch.Dashboard.Components.Shared.CopyableId>().Instance.Value
                .Should().Be(fullId, "the snapshot surfaces the full execution id, not the abbreviation");
            cut.Find(".copyable-id__value").TextContent.Should().Be(fullId);
        });
    }

    [Fact]
    public void Render_Snapshot_WithOutputs_ShowsOutputsPanel()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        var exec = Snapshot("execA", JobStatus.Completed) with
        {
            Outputs = new Dictionary<string, object?> { ["orderId"] = "8264" },
        };
        client.GetExecutionAsync("execA", Arg.Any<CancellationToken>()).Returns(Task.FromResult<JobExecution?>(exec));

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, "execA"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Outputs");
            cut.Markup.Should().Contain("orderId").And.Contain("8264");
        });
    }

    [Fact]
    public void Render_Snapshot_WithParameters_ShowsInputParametersPanel()
    {
        // The consumer side of step-output forwarding: a step's Input parameters (batch-initial + values
        // forwarded from earlier steps + its own static parameters) render on its execution detail, so a
        // downstream step shows the values an upstream step produced.
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        var exec = Snapshot("execA", JobStatus.Completed) with
        {
            Parameters = new Dictionary<string, object?> { ["invoiceId"] = "INV-42" },
        };
        client.GetExecutionAsync("execA", Arg.Any<CancellationToken>()).Returns(Task.FromResult<JobExecution?>(exec));

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, "execA"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Input parameters");
            cut.Markup.Should().Contain("invoiceId").And.Contain("INV-42");
        });
    }

    [Fact]
    public void Render_Snapshot_WithoutOutputs_OmitsOutputsPanel()
    {
        // Additive / zero-regression: an execution with no outputs renders no Outputs card.
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.GetExecutionAsync("execA", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JobExecution?>(Snapshot("execA", JobStatus.Completed)));

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, "execA"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Snapshot"));
        cut.Markup.Should().NotContain("Outputs", "no Outputs card when the execution recorded none");
    }

    [Fact]
    public void Render_NotFound_ShowsEmptyState()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.GetExecutionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JobExecution?>(null));

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, "ghost"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Execution not found"));
    }

    /// <summary>
    /// Detail page hosts a private <c>RecordingClient</c>-equivalent: a real-recording
    /// <see cref="UKBatch.Dashboard.Clients.IUKBatchClient"/> stub that captures subscribed
    /// event handlers so the test can fire a stale state event to assert guard behavior.
    /// </summary>
    private sealed class RecordingDetailClient : UKBatch.Dashboard.Clients.IUKBatchClient
    {
        public UKBatch.Dashboard.Configuration.UKBatchServiceDescriptor Service { get; } = new()
        {
            Name = "rec",
            BaseUrl = new Uri("http://localhost:5000/api/"),
        };
        public UKBatch.Dashboard.UKBatchClientState State => UKBatch.Dashboard.UKBatchClientState.Connected;
#pragma warning disable CS0067 // events declared to satisfy interface; only ExecutionStateChanged is fired by these tests
        public event Func<UKBatch.Dashboard.UKBatchClientState, Task>? StateChanged;
        public event Func<JobExecution, Task>? ExecutionStateChanged;
        public event Func<ProgressBeat, Task>? ProgressUpdated;
        public event Func<PendingApproval, Task>? ApprovalRequested;
        public event Func<BatchCompletionSummary, Task>? BatchCompleted;
#pragma warning restore CS0067

        public JobExecution? InitialSnapshot { get; set; }

        public Task RaiseExecutionStateChangedAsync(JobExecution exec)
        {
            var handler = ExecutionStateChanged;
            return handler is null
                ? Task.CompletedTask
                : Task.WhenAll(handler.GetInvocationList()
                    .Cast<Func<JobExecution, Task>>()
                    .Select(h => h(exec)));
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<UKBatch.Api.Common.PageEnvelope<UKBatch.Api.Jobs.JobDefinitionDto>> ListJobsAsync(int offset, int limit, bool? partitioned, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Jobs.JobDefinitionDto?> GetJobAsync(string jobName, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> TriggerJobAsync(string jobName, IReadOnlyDictionary<string, object?>? parameters, string? triggeredBy, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Common.PageEnvelope<UKBatch.Api.Batches.BatchDefinitionDto>> ListBatchesAsync(int offset, int limit, string? nameContains, UKBatch.Abstractions.Batches.BatchSource? source, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Batches.BatchDefinitionDto?> GetBatchByIdAsync(string definitionId, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Batches.BatchDefinitionDto?> GetBatchByNameAsync(string name, UKBatch.Abstractions.Batches.BatchSource? source, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> RunBatchByIdAsync(string definitionId, IReadOnlyDictionary<string, object?>? initialParameters, string? triggeredBy, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Common.PageEnvelope<JobExecution>> GetBatchRunStatusAsync(string batchRunId, int offset, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Batches.BatchDefinitionDto> CreateBatchAsync(UKBatch.Api.Batches.CreateBatchRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Batches.BatchDefinitionDto> UpdateBatchAsync(string definitionId, UKBatch.Api.Batches.UpdateBatchRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteBatchAsync(string definitionId, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Common.PageEnvelope<BatchRun>> QueryRunsAsync(string? batchDefinitionId, bool includeRunning, int offset, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelRunAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task<string> RetryRunAsync(string batchRunId, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task SetScheduleEnabledAsync(string definitionId, bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken ct) => Task.FromResult(InitialSnapshot);
        public Task<UKBatch.Api.Common.PageEnvelope<JobExecution>> QueryExecutionsAsync(UKBatch.Api.Executions.JobQueryRequest query, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelExecutionAsync(string executionId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<UKBatch.Api.Approvals.PendingApprovalDto>> ListApprovalsAsync(string? role, CancellationToken ct) => throw new NotImplementedException();
        public Task ApproveAsync(string approvalId, string? note, CancellationToken ct) => throw new NotImplementedException();
        public Task RejectAsync(string approvalId, string reason, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<UKBatch.Api.Approvals.ApprovalGateViewDto>> ListBatchGatesAsync(string batchId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<UKBatch.Abstractions.Workers.WorkerInfo>> GetWorkersAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task SubscribeToExecutionAsync(string executionId, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromExecutionAsync(string executionId, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeToBatchAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromBatchAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeToJobAsync(string jobName, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromJobAsync(string jobName, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StaleRunningAfterCompleted_DoesNotRegressDetailMarkup()
    {
        // sibling test: the Detail page hosts a duplicate of the row-level
        // monotonic guard. Once the snapshot lands as Completed, a stale Running event MUST NOT
        // regress the rendered Status.
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = new RecordingDetailClient
        {
            InitialSnapshot = Snapshot("execStale", JobStatus.Completed),
        };

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Id, "execStale"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("COMPLETED"));

        // Stale Running event arrives AFTER the snapshot is Completed.
        await client.RaiseExecutionStateChangedAsync(new JobExecution
        {
            ExecutionId = "execStale",
            JobName = "test-job",
            Status = JobStatus.Running,
            Parameters = new Dictionary<string, object?>(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = 1,
            MaxRetries = 3,
            Processed = 0,
            Failed = 0,
        });

        cut.Markup.Should().Contain("COMPLETED",
 "Detail page MUST NOT regress past terminal Completed on stale Running event.");
        cut.Markup.Should().NotContain("RUNNING");
    }
}
