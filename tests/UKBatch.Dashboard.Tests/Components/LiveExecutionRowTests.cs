using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Shared;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.Models;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Components;

public sealed class LiveExecutionRowTests : TestContext
{
    private static ExecutionRowViewModel ModelFor(string id, JobStatus status, int attempt = 1) => new()
    {
        ExecutionId = id,
        JobName = "test-job",
        Status = status,
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = attempt,
        MaxRetries = 3,
        Processed = 0,
        Failed = 0,
    };

    private static JobExecution ExecFor(string id, JobStatus status, int attempt = 1) => new()
    {
        ExecutionId = id,
        JobName = "test-job",
        Status = status,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = attempt,
        MaxRetries = 3,
        Processed = 0,
        Failed = 0,
    };

    /// <summary>
    /// Mock-recording client that records subscribed event handlers, so tests can invoke them
    /// directly (NSubstitute's <c>Raise.Event</c> does not support <c>Func&lt;T, Task&gt;</c>
    /// event handler signatures on interfaces — invoking the captured handler directly is the
    /// minimum-friction path to actually exercise the LiveExecutionRow guard).
    /// </summary>
    private sealed class RecordingClient : IUKBatchClient
    {
        public UKBatchServiceDescriptor Service { get; } = new()
        {
            Name = "rec",
            BaseUrl = new Uri("http://localhost:5000/api/"),
        };

        public UKBatchClientState State => UKBatchClientState.Connected;

#pragma warning disable CS0067 // events declared to satisfy interface; only ExecutionStateChanged is fired by these tests
        public event Func<UKBatchClientState, Task>? StateChanged;

        public event Func<JobExecution, Task>? ExecutionStateChanged;
        public event Func<ProgressBeat, Task>? ProgressUpdated;
        public event Func<PendingApproval, Task>? ApprovalRequested;
        public event Func<BatchCompletionSummary, Task>? BatchCompleted;
#pragma warning restore CS0067

        public int SubscribeToExecutionCount { get; private set; }
        public int UnsubscribeFromExecutionCount { get; private set; }
        public string? LastSubscribedExecutionId { get; private set; }
        public string? LastUnsubscribedExecutionId { get; private set; }

        public Task SubscribeToExecutionAsync(string executionId, CancellationToken ct)
        {
            SubscribeToExecutionCount++;
            LastSubscribedExecutionId = executionId;
            return Task.CompletedTask;
        }

        public Task UnsubscribeFromExecutionAsync(string executionId, CancellationToken ct)
        {
            UnsubscribeFromExecutionCount++;
            LastUnsubscribedExecutionId = executionId;
            return Task.CompletedTask;
        }

        /// <summary>Test hook — fires the ExecutionStateChanged event the row subscribed to.</summary>
        public Task RaiseExecutionStateChangedAsync(JobExecution exec)
        {
            var handler = ExecutionStateChanged;
            return handler is null
                ? Task.CompletedTask
                : Task.WhenAll(handler.GetInvocationList()
                    .Cast<Func<JobExecution, Task>>()
                    .Select(h => h(exec)));
        }

        // ── Unused surface — sealed Task.FromResult stubs ─────────────────────────────────
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
        public Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken ct) => throw new NotImplementedException();
        public Task<UKBatch.Api.Common.PageEnvelope<JobExecution>> QueryExecutionsAsync(UKBatch.Api.Executions.JobQueryRequest query, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelExecutionAsync(string executionId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<UKBatch.Api.Approvals.PendingApprovalDto>> ListApprovalsAsync(string? role, CancellationToken ct) => throw new NotImplementedException();
        public Task ApproveAsync(string approvalId, string? note, CancellationToken ct) => throw new NotImplementedException();
        public Task RejectAsync(string approvalId, string reason, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<UKBatch.Abstractions.Workers.WorkerInfo>> GetWorkersAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task SubscribeToBatchAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromBatchAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeToJobAsync(string jobName, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromJobAsync(string jobName, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IUKBatchClient BuildClient()
    {
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(UKBatchClientState.Connected);
        return client;
    }

    private void RegisterFactory(IUKBatchClient client)
    {
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);
        Services.AddSingleton(factory);
    }

    private RecordingClient RegisterRecordingClient()
    {
        var client = new RecordingClient();
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);
        Services.AddSingleton(factory);
        return client;
    }

    [Fact]
    public void Render_NoSubscribe_RendersInitialModel()
    {
        RegisterFactory(BuildClient());
        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("exec-12345678", JobStatus.Running))
            .Add(r => r.SubscribeToLive, false));

        cut.Markup.Should().Contain("RUNNING");
        cut.Markup.Should().Contain("exec-123");
    }

    [Fact]
    public async Task SubscribeFirstThenFetch_SubscribeCalledBeforeAnyEventDelivery()
    {
        var client = BuildClient();
        RegisterFactory(client);

        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("execA", JobStatus.Pending))
            .Add(r => r.SubscribeToLive, true));

        await client.Received(1).SubscribeToExecutionAsync("execA", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleRunningAfterCompleted_DoesNotRegressMarkup()
    {
        // Completed -> stale Running event MUST NOT regress the row past terminal.
        // The previous version of this test asserted the static Rank lookup table only — that
        // never exercised the live guard. This version subscribes the row to the client, then
        // raises a stale Running event through the captured handler and asserts the markup stays
        // on the terminal Completed state.
        var client = RegisterRecordingClient();
        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("execX", JobStatus.Completed))
            .Add(r => r.SubscribeToLive, true));

        // Initial render — Completed terminal state.
        cut.Markup.Should().Contain("COMPLETED");
        client.SubscribeToExecutionCount.Should().Be(1);

        // Raise a stale Running event AFTER the row subscribed.
        await client.RaiseExecutionStateChangedAsync(ExecFor("execX", JobStatus.Running));

        // monotonic guard must reject the stale event: Running (rank 1) < Completed (rank 3).
        cut.Markup.Should().Contain("COMPLETED",
 "stale Running event MUST NOT regress row past terminal Completed state.");
        cut.Markup.Should().NotContain("RUNNING");
    }

    [Fact]
    public async Task StaleEventForDifferentExecutionId_Ignored()
    {
        // Row filters events by ExecutionId — a Completed event for an unrelated id must NOT
        // displace the current row.
        var client = RegisterRecordingClient();
        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("execA", JobStatus.Running))
            .Add(r => r.SubscribeToLive, true));

        cut.Markup.Should().Contain("RUNNING");

        // Foreign-id event.
        await client.RaiseExecutionStateChangedAsync(ExecFor("execB", JobStatus.Completed));

        cut.Markup.Should().Contain("RUNNING",
            "row must only react to events for its own ExecutionId");
        cut.Markup.Should().NotContain("COMPLETED");
    }

    [Fact]
    public async Task SameStatusLowerAttempt_DoesNotRegressMarkup()
    {
        // guard tie-breaker: same Status + smaller AttemptNumber must be rejected.
        var client = RegisterRecordingClient();
        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("execY", JobStatus.Running, attempt: 3))
            .Add(r => r.SubscribeToLive, true));

        cut.Markup.Should().Contain("RUNNING");

        // Stale attempt — older attempt fires after newer one.
        await client.RaiseExecutionStateChangedAsync(ExecFor("execY", JobStatus.Running, attempt: 1));

        cut.Markup.Should().Contain("RUNNING",
 "stale same-status earlier-attempt event MUST NOT overwrite the current row");
    }

    [Fact]
    public async Task ProgressionToTerminal_UpdatesMarkup()
    {
        // Sanity: a legitimate forward transition (Running -> Completed) MUST be honored — the
        // guard only blocks regression.
        var client = RegisterRecordingClient();
        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("execZ", JobStatus.Running))
            .Add(r => r.SubscribeToLive, true));

        cut.Markup.Should().Contain("RUNNING");

        await client.RaiseExecutionStateChangedAsync(ExecFor("execZ", JobStatus.Completed));

        cut.Markup.Should().Contain("COMPLETED",
            "forward transition Running -> Completed must propagate (higher rank wins)");
    }

    [Fact]
    public void ParentPushesForwardStatus_NotSubscribed_RowAdoptsNewModel()
    {
        // BUG 2 (cross-service stuck-Running) REGRESSION. In RunDetail mode the row is
        // SubscribeToLive=false: it has NO hub handler of its own and depends ENTIRELY on the parent
        // re-rendering it with a new InitialModel of the SAME ExecutionId (the parent's @key). The
        // page mints a Running cross-service shadow row, then UPDATES it to Completed and re-renders.
        // The old OnParametersSet froze the row whenever the ExecutionId matched (`id-equal ⇒ skip`),
        // so the table stayed RUNNING while the DAG (built from the same row list) showed COMPLETED.
        RegisterFactory(BuildClient());

        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("xsvc-1", JobStatus.Running))
            .Add(r => r.SubscribeToLive, false));

        cut.Markup.Should().Contain("RUNNING");

        // Parent pushes the terminal update for the SAME execution id (mirrors RunDetail replacing
        // _executionRows[idx] then re-rendering the @key-stable row).
        cut.SetParametersAndRender(p => p
            .Add(r => r.InitialModel, ModelFor("xsvc-1", JobStatus.Completed)));

        cut.Markup.Should().Contain("COMPLETED",
            "a not-subscribed row MUST adopt the parent's forward InitialModel update (BUG 2 fix)");
        cut.Markup.Should().NotContain("RUNNING");
    }

    [Fact]
    public void ParentPushesStaleStatus_NotSubscribed_RowKeepsTerminal()
    {
        // BUG 2 fix must NOT regress: the OnParametersSet adoption is monotonic (rank). If the
        // parent somehow re-renders with an older status for the same id, the row keeps the terminal.
        RegisterFactory(BuildClient());

        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("xsvc-2", JobStatus.Completed))
            .Add(r => r.SubscribeToLive, false));

        cut.Markup.Should().Contain("COMPLETED");

        cut.SetParametersAndRender(p => p
            .Add(r => r.InitialModel, ModelFor("xsvc-2", JobStatus.Running)));

        cut.Markup.Should().Contain("COMPLETED",
            "OnParametersSet adoption is monotonic — a stale parent push must not regress the row");
        cut.Markup.Should().NotContain("RUNNING");
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesAndLeavesGroup()
    {
        var client = BuildClient();
        RegisterFactory(client);

        var cut = RenderComponent<LiveExecutionRow>(p => p
            .Add(r => r.ServiceName, "svc")
            .Add(r => r.InitialModel, ModelFor("execD", JobStatus.Running))
            .Add(r => r.SubscribeToLive, true));

        await cut.Instance.DisposeAsync();

        await client.Received(1).UnsubscribeFromExecutionAsync("execD", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void StatusRankMatrix_AllTerminalStatesShareTopRank()
    {
        // Completed / Failed / Cancelled all sit at top rank so terminal can only be
        // displaced by higher rank (impossible) or same status with higher AttemptNumber.
        // the rank rule now lives in the shared UKBatch.Dashboard.Models.JobStatusRank
        // (single source of truth) — LiveExecutionRow + Executions/Detail + RunDetail all call it.
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.Completed).Should().Be(3);
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.Failed).Should().Be(3);
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.Cancelled).Should().Be(3);
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.Cancelling).Should().Be(2);
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.Running).Should().Be(1);
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.AwaitingApproval).Should().Be(1);
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.Pending).Should().Be(0);
        UKBatch.Dashboard.Models.JobStatusRank.Rank(JobStatus.Scheduled).Should().Be(0);
    }
}
