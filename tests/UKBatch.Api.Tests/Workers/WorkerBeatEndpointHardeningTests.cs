using System.Net;
using System.Text;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Workers;

/// <summary>
/// Input-hardening tests for <c>POST /api/workers/beat</c> over the WAF. The endpoint is anonymous
/// (mounted under <c>/api</c>), so a malformed or hostile beat must be rejected with a 400
/// ValidationProblem rather than crashing the registry or growing it without bound. Of particular note:
/// an explicit JSON <c>null</c> for <c>jobs</c>/<c>tags</c> must be tolerated (the wire DTO's init
/// default of <c>[]</c> does not protect against explicit null) and must NOT surface as a 500.
/// </summary>
public sealed class WorkerBeatEndpointHardeningTests : IClassFixture<SampleRestApiFactory>
{
    private static readonly Uri BeatUri = new("/api/workers/beat", UriKind.Relative);
    private readonly SampleRestApiFactory _factory;

    public WorkerBeatEndpointHardeningTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PostBeat_JobsExplicitNull_IsAccepted_NoServerError()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsync(BeatUri, RawJson("""{ "name": "w1", "jobs": null }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "an explicit null jobs array normalizes to empty — it must not throw a NullReferenceException (500)");
    }

    [Fact]
    public async Task PostBeat_TagsExplicitNull_IsAccepted_NoServerError()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsync(BeatUri, RawJson("""{ "name": "w1", "tags": null }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "an explicit null tags array normalizes to empty");
    }

    [Fact]
    public async Task PostBeat_BothListsExplicitNull_IsAccepted_NoServerError()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsync(BeatUri, RawJson("""{ "name": "w1", "jobs": null, "tags": null }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PostBeat_NameTooLong_Returns400()
    {
        using var client = _factory.CreateClient();
        var longName = new string('x', 201); // > the 200-char name cap

        var resp = await client.PostAsync(BeatUri,
            RawJson($$"""{ "name": "{{longName}}", "jobs": [], "tags": [] }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a worker name over 200 chars is rejected");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Name", "the validation problem names the offending field");
    }

    [Fact]
    public async Task PostBeat_BlankName_Returns400()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsync(BeatUri, RawJson("""{ "name": "   ", "jobs": [], "tags": [] }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a whitespace-only name is rejected");
    }

    [Fact]
    public async Task PostBeat_JobNameTooLong_Returns400()
    {
        using var client = _factory.CreateClient();
        var longJob = new string('j', 201);

        var resp = await client.PostAsync(BeatUri,
            RawJson($$"""{ "name": "w1", "jobs": ["{{longJob}}"], "tags": [] }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an over-length job name is rejected");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Jobs");
    }

    [Fact]
    public async Task PostBeat_BlankJobEntry_Returns400()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsync(BeatUri,
            RawJson("""{ "name": "w1", "jobs": ["ok", "  "], "tags": [] }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a blank job entry is rejected");
    }

    [Fact]
    public async Task PostBeat_TagTooLong_Returns400()
    {
        using var client = _factory.CreateClient();
        var longTag = new string('t', 101); // > the 100-char tag cap

        var resp = await client.PostAsync(BeatUri,
            RawJson($$"""{ "name": "w1", "jobs": [], "tags": ["{{longTag}}"] }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an over-length tag is rejected");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Tags");
    }

    [Fact]
    public async Task PostBeat_ValidPayload_IsAccepted()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsync(BeatUri,
            RawJson("""{ "name": "good-worker", "jobs": ["GenerateInvoice"], "tags": ["billing"], "status": "Online" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "a well-formed beat ingests with 202");
    }
}
