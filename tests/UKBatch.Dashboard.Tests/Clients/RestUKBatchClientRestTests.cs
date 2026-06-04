using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Executions;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.Tests.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Clients;

/// <summary>
/// RestUKBatchClient REST method coverage.
/// </summary>
public sealed class RestUKBatchClientRestTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public RestUKBatchClientRestTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ListJobsAsync_ReturnsPagedDtos()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var page = await client.ListJobsAsync(0, 50, partitioned: null, cts.Token);
        page.Should().NotBeNull();
        page.Items.Should().NotBeEmpty("Sample.RestApi registers several jobs at startup");
        page.Offset.Should().Be(0);
    }

    [Fact]
    public async Task GetJobAsync_KnownJob_ReturnsDto()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var page = await client.ListJobsAsync(0, 1, partitioned: null, cts.Token);
        var firstName = page.Items[0].Name;
        var dto = await client.GetJobAsync(firstName, cts.Token);
        dto.Should().NotBeNull();
        dto!.Name.Should().Be(firstName);
    }

    [Fact]
    public async Task GetJobAsync_Unknown_ReturnsNull()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dto = await client.GetJobAsync("Nonexistent.Job.That.Does.Not.Exist", cts.Token);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task TriggerJobAsync_KnownJob_ReturnsExecutionId()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execId = await client.TriggerJobAsync("Sample.RestApi.Jobs.InvoiceGenerationJob", parameters: null, triggeredBy: "test", cts.Token);
        execId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TriggerJobAsync_UnknownJob_ThrowsUKBatchClientException()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Func<Task> act = () => client.TriggerJobAsync("Nonexistent.Job", parameters: null, triggeredBy: null, cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListBatchesAsync_ReturnsPagedDtos()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var page = await client.ListBatchesAsync(0, 50, nameContains: null, source: null, cts.Token);
        page.Should().NotBeNull();
        page.Items.Should().NotBeEmpty("Sample.RestApi registers several batch definitions");
    }

    [Fact]
    public async Task GetBatchByIdAsync_Unknown_ReturnsNull()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dto = await client.GetBatchByIdAsync("nonexistent-batch-def-id", cts.Token);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task GetBatchByNameAsync_Unknown_ReturnsNull()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dto = await client.GetBatchByNameAsync("nonexistent-batch-name", source: BatchSource.Code, cts.Token);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task RunBatchByIdAsync_KnownBatch_ReturnsRunId()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var page = await client.ListBatchesAsync(0, 50, nameContains: null, source: null, cts.Token);
        var firstId = page.Items[0].Id;
        var batchRunId = await client.RunBatchByIdAsync(firstId, initialParameters: null, triggeredBy: "test", cts.Token);
        batchRunId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task QueryExecutionsAsync_ReturnsPagedSnapshots()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var query = new JobQueryRequest { Limit = 20 };
        var page = await client.QueryExecutionsAsync(query, cts.Token);
        page.Should().NotBeNull();
        page.Offset.Should().Be(0);
    }

    [Fact]
    public async Task GetExecutionAsync_Unknown_ReturnsNull()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dto = await client.GetExecutionAsync("nonexistent-exec-id", cts.Token);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task ListApprovalsAsync_ReturnsList_StripsPageEnvelope()
    {
        // contract: client unwraps the artificial PageEnvelope returned by /approvals.
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var approvals = await client.ListApprovalsAsync(role: null, cts.Token);
        approvals.Should().NotBeNull();
        // Result is IReadOnlyList<PendingApprovalDto> — not a PageEnvelope.
        approvals.Should().BeAssignableTo<IReadOnlyList<UKBatch.Api.Approvals.PendingApprovalDto>>();
    }

    [Fact]
    public async Task CancelExecutionAsync_UnknownExecution_ThrowsUKBatchClientException()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Func<Task> act = () => client.CancelExecutionAsync("nonexistent-exec-id", cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ApproveAsync_UnknownApproval_ThrowsUKBatchClientException()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Func<Task> act = () => client.ApproveAsync("nonexistent-approval-id", note: "noop", cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();
        ex.Which.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RejectAsync_UnknownApproval_ThrowsUKBatchClientException()
    {
        await using var client = RestUKBatchClientFactory.BuildRestOnly(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Func<Task> act = () => client.RejectAsync("nonexistent-approval-id", reason: "test", cts.Token);
        var ex = await act.Should().ThrowAsync<UKBatchClientException>();
        ex.Which.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }
}
