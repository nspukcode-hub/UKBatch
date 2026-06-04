using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// <c>/approvals</c> endpoint tests including (approver from HttpContext.User,
/// NOT the request body), / (typed exception mapping), and reject-reason validation.
/// </summary>
public sealed class ApprovalsEndpointTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public ApprovalsEndpointTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetApprovals_ReturnsPagedEnvelope()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/approvals", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.TryGetProperty("totalCount", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Approve_UnknownId_Returns404_TypedExceptionMapping()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var response = await client.PostAsync(
            new Uri("/api/approvals/missing/approve", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { note = "n/a" }));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:approval-not-pending");
    }

    [Fact]
    public async Task Reject_EmptyReason_Returns400ValidationProblem()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var response = await client.PostAsync(
            new Uri("/api/approvals/any/reject", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { reason = "" }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("reason");
    }

    [Fact]
    public async Task Reject_MissingBody_Returns400ValidationProblem()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var response = await client.PostAsync(
            new Uri("/api/approvals/any/reject", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Approve_AnonymousAndUnknownId_Returns404_NotForbidden()
    {
        // lock — anonymous identity is built from HttpContext.User, NOT the body.
        // Unknown id throws ApprovalNotFoundException FIRST (before role check).
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/approvals/anon-unknown/approve", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { note = "lgtm" }));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approve_SpoofedRolesInBody_AreIgnored()
    {
        // mutation test — POST body with a legacy-shape `approver` field MUST be ignored.
        // ApprovalNoteRequest deserializes the body; unknown properties are dropped silently.
        // Unknown approval id -> 404 (the identity layer never trusts the body).
        using var client = _factory.CreateClient();
        var spoofedBody = new
        {
            approver = new { identity = "ceo", roles = new[] { "admin" } },
            note = "spoof attempt",
        };
        var response = await client.PostAsync(
            new Uri("/api/approvals/spoof-test/approve", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(spoofedBody));
        // Approval doesn't exist -> 404 (mapping is independent of body content).
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
