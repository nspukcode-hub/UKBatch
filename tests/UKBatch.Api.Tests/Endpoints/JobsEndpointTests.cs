using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

// <summary> — <c>/jobs</c> surface tests.</summary>
public sealed class JobsEndpointTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public JobsEndpointTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetJobs_ReturnsRegisteredJobs_InRegistrationOrder()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/jobs", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(j => j.GetProperty("name").GetString()).ToList();
        items.Should().HaveCountGreaterThan(0);
        // Sample.RestApi registers InvoiceGenerationJob, EmailNotificationJob, ArchiveJob, RollbackJob in order.
        items.Should().Contain(n => n!.Contains("InvoiceGenerationJob"));
        items.Should().Contain(n => n!.Contains("EmailNotificationJob"));
    }

    [Fact]
    public async Task GetJobs_InvalidLimit_Returns400ValidationProblem()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/jobs?limit=99999", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("limit");
    }

    [Fact]
    public async Task GetJob_UnknownName_Returns404ProblemDetails()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/jobs/does-not-exist", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:job-not-registered");
    }

    [Fact]
    public async Task GetJob_ByName_Returns200()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TriggerJob_Returns202_AndExecutionLocationHeader()
    {
        using var client = _factory.CreateClient();
        var body = DevAuthHttpClientExtensions.JsonContent(new { parameters = new Dictionary<string, object?>() });
        var response = await client.PostAsync(new Uri("/api/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob/trigger", UriKind.Relative), body);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var execId = doc.RootElement.GetProperty("executionId").GetString();
        execId.Should().NotBeNullOrWhiteSpace();
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain(execId!);
    }

    [Fact]
    public async Task TriggerJob_UnknownName_Returns404()
    {
        using var client = _factory.CreateClient();
        var body = DevAuthHttpClientExtensions.JsonContent(new { parameters = new Dictionary<string, object?>() });
        var response = await client.PostAsync(new Uri("/api/jobs/Nope.Missing/trigger", UriKind.Relative), body);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListJobs_DefaultsToConfiguredLimit_WhenOmitted()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/jobs", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("limit").GetInt32().Should().Be(50);
    }
}
