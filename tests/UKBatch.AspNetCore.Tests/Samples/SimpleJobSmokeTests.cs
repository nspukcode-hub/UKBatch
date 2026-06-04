using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using UKBatch.AspNetCore.Tests.Helpers;
using Xunit;

namespace UKBatch.AspNetCore.Tests.Samples;

/// <summary>
/// Smoke tests for the <c>Sample.SimpleJob</c> ASP.NET Core host. Boots via
/// <see cref="WebApplicationFactory{T}"/> rooted on the sample's <c>Program</c>; hits each
/// documented endpoint.
/// </summary>
public sealed class SimpleJobSmokeTests : IClassFixture<WebApplicationFactory<Sample.SimpleJob.Program>>
{
    private readonly WebApplicationFactory<Sample.SimpleJob.Program> _factory;

    public SimpleJobSmokeTests(WebApplicationFactory<Sample.SimpleJob.Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        var body = await response.ShouldBeAsync(HttpStatusCode.OK);
        body.Should().Be("Healthy");
    }

    [Fact]
    public async Task TriggerHelloEndpoint_ProducesExecutionWithTriggeredBy()
    {
        using var client = _factory.CreateClient();
        client.WithDevAuth("alice");
        var response = await client.GetAsync(new Uri("/trigger/hello", UriKind.Relative));
        var body = await response.ShouldBeAsync(HttpStatusCode.OK);
        body.Should().Contain("\"triggeredBy\":\"alice\"");
    }
}
