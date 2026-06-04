using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.OpenApi;

// <summary> / — OpenAPI document shape regressions.</summary>
public sealed class OpenApiDocumentTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public OpenApiDocumentTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<JsonDocument> FetchAsync()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    [Fact]
    public async Task OpenApiDocument_HasExpectedPaths()
    {
        using var doc = await FetchAsync();
        var paths = doc.RootElement.GetProperty("paths");
        var keys = paths.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().Contain("/api/jobs");
        keys.Should().Contain("/api/jobs/{name}");
        keys.Should().Contain("/api/batches");
        keys.Should().Contain("/api/batches/by-id/{id}");
        keys.Should().Contain("/api/batches/by-name/{name}");
        keys.Should().Contain("/api/approvals");
        keys.Should().Contain("/api/executions/query");
    }

    [Fact]
    public async Task OpenApiDocument_EnumsAsStrings()
    {
        using var doc = await FetchAsync();
        // Find a schema referencing JobStatus and verify it is rendered as a string enum.
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var foundEnum = false;
        foreach (var schemaProp in schemas.EnumerateObject())
        {
            if (schemaProp.Value.TryGetProperty("enum", out var enumNode) && schemaProp.Value.TryGetProperty("type", out var typeNode))
            {
                if (typeNode.GetString() == "string")
                {
                    foundEnum = true;
                    var values = enumNode.EnumerateArray().Select(v => v.GetString()).ToList();
                    values.Should().AllSatisfy(v => v.Should().NotMatchRegex("^[0-9]+$"));
                }
            }
        }
        foundEnum.Should().BeTrue("EnumStringTransformer must render at least one enum as string");
    }

    [Fact]
    public async Task OpenApi_NoApproverFieldInApprovalRequestSchema()
    {
        // schema lock-down: ApprovalNoteRequest must have ONLY `note`; ApprovalReasonRequest only `reason`.
        using var doc = await FetchAsync();
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        // The schema names are typically the C# type names without namespace.
        schemas.TryGetProperty("ApprovalNoteRequest", out var noteSchema).Should().BeTrue();
        var noteProps = noteSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        noteProps.Should().NotContain("approver");
        noteProps.Should().NotContain("identity");
        noteProps.Should().NotContain("roles");

        schemas.TryGetProperty("ApprovalReasonRequest", out var reasonSchema).Should().BeTrue();
        var reasonProps = reasonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        reasonProps.Should().NotContain("approver");
    }

    [Fact]
    public async Task OpenApi_NoHubBackpressureWarningInClientMethods()
    {
        // / lock — IJobStatusHubClient does NOT expose HubBackpressureWarning.
        // The IJobStatusHubClient type is a SignalR client RPC contract; not all RPCs
        // appear in OpenAPI (the hub itself isn't documented), but we assert there is
        // no schema reference for HubBackpressureWarning anywhere.
        using var doc = await FetchAsync();
        var json = doc.RootElement.GetRawText();
        json.Should().NotContain("HubBackpressureWarning", "A18 / B6 deferral: this method was dropped in v0.1");
    }

    [Fact]
    public async Task Json_UsesCamelCase()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"items\"");
        body.Should().Contain("\"totalCount\"");
        body.Should().NotContain("\"Items\"");
        body.Should().NotContain("\"TotalCount\"");
    }

    [Fact]
    public async Task OpenApiDocument_ProblemDetailsResponses_Annotated()
    {
        // spec #29: ProblemDetailsResponseTransformer must annotate every operation
        // with 400/403/404/409/500 Problem Details responses. We verify at least one operation
        // carries all five status codes with the application/problem+json content type.
        using var doc = await FetchAsync();
        var paths = doc.RootElement.GetProperty("paths");
        // Pick the first GET-handling operation we find.
        var foundOperation = false;
        foreach (var pathProp in paths.EnumerateObject())
        {
            foreach (var verbProp in pathProp.Value.EnumerateObject())
            {
                if (!verbProp.Value.TryGetProperty("responses", out var responses)) continue;
                var have400 = responses.TryGetProperty("400", out var r400);
                var have403 = responses.TryGetProperty("403", out var r403);
                var have404 = responses.TryGetProperty("404", out var r404);
                var have409 = responses.TryGetProperty("409", out var r409);
                var have500 = responses.TryGetProperty("500", out var r500);
                if (have400 && have403 && have404 && have409 && have500)
                {
                    foundOperation = true;
                    // Spot-check one of the responses for the application/problem+json content type.
                    var content = r404.GetProperty("content");
                    content.TryGetProperty("application/problem+json", out _).Should().BeTrue(
 " #29: ProblemDetailsResponseTransformer must use application/problem+json.");
                    _ = r400; _ = r403; _ = r409; _ = r500; // suppress unused.
                    break;
                }
            }
            if (foundOperation) break;
        }
        foundOperation.Should().BeTrue(
 " #29: at least one operation should have 400/403/404/409/500 Problem Details annotations.");
    }
}
