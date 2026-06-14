using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Hub;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// End-to-end proof of the gate-failure colouring fix over the real WAF: a batch parked on an approval
/// gate that is REJECTED ends Failed, and the SignalR <see cref="BatchCompletionSummary"/>
/// reports <see cref="JobStatus.Failed"/> — even though no <see cref="JobExecution"/> row is Failed (a
/// gate has no row). Exercises the whole chain: REST reject → ApprovalGateService →
/// BatchExecutor rethrow → JobRunner closure verdict → hub FinalStatus override → wire summary.
/// <para>
/// Uses <c>wildcard-approval-pipeline</c> (OnTimeout=Hold + 5-minute timeout + StopOnFailure): it parks
/// indefinitely so there is NO auto-approve race against the reject, making the Failed assertion
/// deterministic. An authenticated operator satisfies its AnyAuthenticatedUser ("*") gate.
/// </para>
/// </summary>
public sealed class GateFailureColoursRunEndToEndTests : IClassFixture<SampleRestApiFactory>
{
    private const string PipelineName = "wildcard-approval-pipeline";

    private readonly SampleRestApiFactory _factory;

    public GateFailureColoursRunEndToEndTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private HubConnection BuildHubConnection()
    {
        var hubUri = new Uri(_factory.Server.BaseAddress, "/api/hubs/jobs");
        return new HubConnectionBuilder()
            .WithUrl(hubUri, opt =>
            {
                opt.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                opt.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    [Theory]
    [InlineData("reject")]
    public async Task GateFailure_ColoursRunFailed_OnTheCompletionSummary(string action)
    {
        await using var connection = BuildHubConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Subscribe to 'all' BEFORE triggering so the BatchCompleted is never missed.
        var pendingBatchId = (string?)null;
        var mySummary = new TaskCompletionSource<BatchCompletionSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            if (pendingBatchId is not null && s.BatchId == pendingBatchId)
            {
                mySummary.TrySetResult(s);
            }
        });
        await connection.StartAsync(cts.Token);
        await connection.InvokeAsync("SubscribeAll", cts.Token);

        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchId = await client.TriggerBatchByNameAsync(PipelineName);
        pendingBatchId = batchId;
        var approvalId = await client.PollForPendingApprovalAsync(batchId);

        // Fail the gate via the operator escape under test.
        var resp = await client.PostAsync(
            new Uri($"/api/approvals/{approvalId}/{action}", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { reason = $"{action} for colouring e2e" }));
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent, $"an authorized {action} succeeds with 204.");

        var winner = await Task.WhenAny(mySummary.Task, Task.Delay(TimeSpan.FromSeconds(55), cts.Token));
        winner.Should().Be(mySummary.Task, "the runtime must signal batch completion after the gate failure.");
        var summary = await mySummary.Task;

        summary.FinalStatus.Should().Be(JobStatus.Failed,
            $"a {action}ed gate ends the batch in failure; the run must surface Failed even though no job row is Failed.");

        await connection.StopAsync(cts.Token);
    }
}
