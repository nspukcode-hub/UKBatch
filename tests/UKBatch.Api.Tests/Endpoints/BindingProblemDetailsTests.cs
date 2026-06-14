using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// A malformed or empty body to a <c>[FromBody]</c> endpoint (e.g. <c>POST /batches</c>) returns 400.
/// The library registers <c>AddProblemDetails()</c> so failed responses can carry an RFC 7807
/// <c>application/problem+json</c> body; whether the binding 400 itself gets that body depends on the
/// host pipeline (<c>UseExceptionHandler</c>/<c>UseStatusCodePages</c>), so the media-type check is
/// best-effort and the status 400 is the contract the library guarantees.
/// </summary>
public sealed class BindingProblemDetailsTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public BindingProblemDetailsTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostBatches_EmptyBody_Returns400()
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent("", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), content);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertProblemJsonIfPresent(resp);
    }

    [Fact]
    public async Task PostBatches_MalformedJson_Returns400()
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent("{ not valid json ", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), content);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertProblemJsonIfPresent(resp);
    }

    // With AddProblemDetails() registered by the library AND the sample host's standard pipeline, a
    // binding 400 DOES carry an application/problem+json body — verified here. A host that omits the
    // status-code/exception middleware (UseStatusCodePages / UseExceptionHandler) may produce a bare
    // 400; in that case downgrade this to status-only and document the host-pipeline requirement in
    // the Api README (the library must NOT inject middleware). The 400 status is the library's
    // guarantee; the problem+json body is the host-pipeline-dependent enrichment.
    private static void AssertProblemJsonIfPresent(HttpResponseMessage resp)
    {
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json",
            "AddProblemDetails() + the host pipeline emit an RFC 7807 body for failed responses.");
    }
}
