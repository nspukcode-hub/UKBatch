using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// dual-mount auth-on group demo. Locks the
/// <c>MapUKBatchApi("Secured").RequireAuthorization()</c> recipe.
/// </summary>
public sealed class SecuredGroupTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public SecuredGroupTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_Get_ApiSecuredBatches_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/secured/batches", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
 "secured mount requires authentication.");
    }

    [Fact]
    public async Task Authenticated_Get_ApiSecuredBatches_Returns200()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var resp = await client.GetAsync(new Uri("/api/secured/batches", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "DevAuth-authenticated user satisfies RequireAuthorization.");
    }

    [Fact]
    public async Task Anonymous_Get_ApiBatches_Still200()
    {
        // Anonymous on the ORIGINAL /api group is unaffected by the secured mount.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/batches", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the anonymous mount preserves behavior unchanged.");
    }

#if NET10_0_OR_GREATER
    // The OpenAPI document endpoint requires net9+; on net8.0 the package ships REST + SignalR
    // without document generation, so this contract is asserted on net10.0 only.
    [Fact]
    public async Task OpenApi_HasBothAnonymousAndSecuredOperations_OperationIdsStableUnderSingleMount()
    {
        // Under dual-mount, OpenAPI must list BOTH operation ids (bare AND prefixed). The bare
        // names MUST remain byte-identical to the single-mount surface — locks the contract that
        // the anonymous mount surface stays stable across the prefix overload change.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var paths = doc.RootElement.GetProperty("paths");

        // Collect every operationId across all paths/verbs.
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pathProp in paths.EnumerateObject())
        {
            foreach (var verbProp in pathProp.Value.EnumerateObject())
            {
                if (verbProp.Value.TryGetProperty("operationId", out var opId))
                {
                    operationIds.Add(opId.GetString()!);
                }
            }
        }

        // Bare names (anonymous mount) MUST remain present.
        operationIds.Should().Contain("ListBatches", "the op id 'ListBatches' must remain on the anonymous mount.");
        operationIds.Should().Contain("ListJobs", "the op id 'ListJobs' must remain on the anonymous mount.");
        // Secured-prefixed names MUST also appear (dual-mount).
        operationIds.Should().Contain("SecuredListBatches", "Item #7: secured mount surfaces prefixed op id.");
        operationIds.Should().Contain("SecuredListJobs", "Item #7: secured mount surfaces prefixed op id.");
    }
#endif
}
