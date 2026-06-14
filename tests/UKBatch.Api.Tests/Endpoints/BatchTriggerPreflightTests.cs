using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// Triggering a batch runs a synchronous pre-flight: a batch referencing an unregistered local job
/// returns 400 <c>application/problem+json</c> (<c>ukbatch:batch-trigger-validation</c>) with an
/// <c>errors</c> extension, instead of accepting 202 and producing zero executions. A fully valid
/// batch still triggers (202).
/// </summary>
public sealed class BatchTriggerPreflightTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public BatchTriggerPreflightTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private static HttpContent BatchWithJob(string name, string jobName) =>
        DevAuthHttpClientExtensions.JsonContent(new
        {
            name,
            source = "Dashboard",
            steps = new[]
            {
                new { stepId = "s1", order = 0, stepType = "Job", job = new { jobName } },
            },
            failurePolicy = "StopOnFailure",
        });

    [Fact]
    public async Task RunById_UnregisteredLocalJob_Returns400ProblemWithErrors()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var name = $"preflight-unregistered-{Guid.NewGuid():N}";
        var create = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), BatchWithJob(name, "this.job.is.not.registered"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var run = await client.PostAsync(new Uri($"/api/batches/by-id/{id}/run?source=Dashboard", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));

        run.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        run.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await run.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:batch-trigger-validation");
        body.Should().Contain("this.job.is.not.registered", "the errors extension must name the unregistered job.");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out var errors).Should().BeTrue("the 400 body carries an errors extension.");
        errors.ValueKind.Should().Be(JsonValueKind.Array);
        errors.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunByName_AllRegistered_Returns202()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var name = $"preflight-valid-{Guid.NewGuid():N}";
        var create = await client.PostAsync(new Uri("/api/batches", UriKind.Relative),
            BatchWithJob(name, "Sample.RestApi.Jobs.InvoiceGenerationJob"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var run = await client.PostAsync(new Uri($"/api/batches/by-name/{name}/run?source=Dashboard", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));

        run.StatusCode.Should().Be(HttpStatusCode.Accepted, "an all-registered batch passes the pre-flight and triggers.");
        var body = await run.Content.ReadAsStringAsync();
        JsonDocument.Parse(body).RootElement.TryGetProperty("batchId", out _).Should().BeTrue();
    }
}
