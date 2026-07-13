using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// Server-side enforcement of required declared parameters on <c>POST /api/jobs/{name}/trigger</c>.
/// Sample.RestApi registers <c>ParameterizedJob</c> with a required <c>orderId</c> plus optional
/// <c>retries</c>/<c>dryRun</c>. A trigger that omits (or nulls) a required parameter is rejected with a
/// 400 <c>ukbatch:job-parameter-validation</c>; an unknown job still 404s (never a 400).
/// </summary>
public sealed class DeclaredParameterEnforcementTests : IClassFixture<SampleRestApiFactory>
{
    private const string Job = "Sample.RestApi.Jobs.ParameterizedJob";
    private readonly SampleRestApiFactory _factory;

    public DeclaredParameterEnforcementTests(SampleRestApiFactory factory) => _factory = factory;

    private static Uri TriggerUri => new($"/api/jobs/{Job}/trigger", UriKind.Relative);

    [Fact]
    public async Task GetJob_ExposesDeclaredParameters_WithKindAsString()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri($"/api/jobs/{Job}", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var declared = doc.RootElement.GetProperty("declaredParameters").EnumerateArray().ToList();
        var orderId = declared.Single(p => p.GetProperty("name").GetString() == "orderId");
        orderId.GetProperty("required").GetBoolean().Should().BeTrue();
        orderId.GetProperty("kind").GetString().Should().Be("String", "the kind enum crosses the wire as its string name");
        var retries = declared.Single(p => p.GetProperty("name").GetString() == "retries");
        retries.GetProperty("kind").GetString().Should().Be("Integer");
        retries.GetProperty("defaultValue").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Trigger_MissingRequired_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(TriggerUri,
            DevAuthHttpClientExtensions.JsonContent(new { parameters = new Dictionary<string, object?>() }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:job-parameter-validation");
        body.Should().Contain("orderId", "the errors array names the missing parameter");
    }

    [Fact]
    public async Task Trigger_RequiredPresentButNull_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(TriggerUri,
            DevAuthHttpClientExtensions.JsonContent(new { parameters = new Dictionary<string, object?> { ["orderId"] = (string?)null } }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a present-but-null value does not satisfy a required parameter (GetRequired rejects null at runtime)");
    }

    [Fact]
    public async Task Trigger_RequiredProvided_Returns202()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(TriggerUri,
            DevAuthHttpClientExtensions.JsonContent(new { parameters = new Dictionary<string, object?> { ["orderId"] = "A-1" } }));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Trigger_OnlyOptionalMissing_Returns202()
    {
        // retries/dryRun are optional — omitting them is fine as long as the required orderId is present.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(TriggerUri,
            DevAuthHttpClientExtensions.JsonContent(new { parameters = new Dictionary<string, object?> { ["orderId"] = "A-2" } }));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Trigger_UnknownJob_Returns404_NotEnforcement400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(new Uri("/api/jobs/Nope.Missing/trigger", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { parameters = new Dictionary<string, object?>() }));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "an unknown job falls through to the not-registered 404, not the enforcement 400");
    }
}
