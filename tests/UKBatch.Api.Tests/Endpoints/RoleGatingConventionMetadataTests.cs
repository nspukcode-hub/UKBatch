using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using UKBatch;
using UKBatch.Api;
using UKBatch.AspNetCore;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// Asserts the role-gating convention on REAL built endpoints (not a hand-built metadata list). This is
/// the runtime proof that the convention runs AFTER each endpoint's access-kind tag is in place: if it
/// used an ordinary <c>Add</c> convention instead of <c>Finally</c>, the tag would not be present when the
/// convention ran, no authorize metadata would be attached, and gating would silently no-op — yet the
/// auth-off tests would still pass. A list-based unit test would false-green that bug; only inspecting the
/// materialised endpoint graph catches it.
/// </summary>
public sealed class RoleGatingConventionMetadataTests
{
    private const string OperatorPolicy = "UKBatch:Operator";
    private const string ViewerPolicy = "UKBatch:Viewer";

    private static async Task<IReadOnlyList<Endpoint>> BuildEndpointsAsync(bool roleGated)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddUKBatchAspNetCore(b => b.AddJob<NoopJob>());
        builder.Services.AddUKBatchApi();

        await using var app = builder.Build();
        var group = app.MapGroup("/api").MapUKBatchApi();
        if (roleGated)
        {
            group.RequireUKBatchRoleAuthorization();
        }

        // Enumerating the data sources materialises the endpoints and runs the group conventions,
        // including the Finally pass the convention relies on.
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
    }

    private static RouteEndpoint Find(IEnumerable<Endpoint> endpoints, string rawTextSuffix, string httpMethod) =>
        endpoints.OfType<RouteEndpoint>().Single(e =>
            e.RoutePattern.RawText!.EndsWith(rawTextSuffix, StringComparison.Ordinal)
            && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(httpMethod) ?? false));

    [Fact]
    public async Task TriggerEndpoint_WhenGated_CarriesOperatorPolicy()
    {
        var endpoints = await BuildEndpointsAsync(roleGated: true);
        var trigger = Find(endpoints, "jobs/{name}/trigger", "POST");

        var authorize = trigger.Metadata.GetMetadata<AuthorizeAttribute>();
        authorize.Should().NotBeNull("the write trigger must carry an authorize requirement under role-gating");
        authorize!.Policy.Should().Be(OperatorPolicy, "a write endpoint maps to the operator policy");
    }

    [Fact]
    public async Task GetJobEndpoint_WhenGated_CarriesViewerPolicy()
    {
        var endpoints = await BuildEndpointsAsync(roleGated: true);
        var getJob = Find(endpoints, "jobs/{name}", "GET");

        var authorize = getJob.Metadata.GetMetadata<AuthorizeAttribute>();
        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(ViewerPolicy, "a read endpoint maps to the viewer policy");
    }

    [Fact]
    public async Task WorkerBeatEndpoint_WhenGated_HasNoAuthorization()
    {
        var endpoints = await BuildEndpointsAsync(roleGated: true);
        var beat = Find(endpoints, "workers/beat", "POST");

        beat.Metadata.GetMetadata<IAuthorizeData>()
            .Should().BeNull("the worker ingest is classified Ingest and is never gated by the convention");
    }

    [Fact]
    public async Task TriggerEndpoint_WhenNotGated_HasNoAuthorization()
    {
        // Without the convention, the endpoint carries no authorize metadata — anonymous, exactly as the
        // default posture. This complements the gated case: it proves the authorize metadata comes from the
        // convention (and not, say, an unconditional attribute on the endpoint).
        var endpoints = await BuildEndpointsAsync(roleGated: false);
        var trigger = Find(endpoints, "jobs/{name}/trigger", "POST");

        trigger.Metadata.GetMetadata<IAuthorizeData>()
            .Should().BeNull("not calling the convention leaves every endpoint anonymous");
    }
}
