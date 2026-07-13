using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Workers;

/// <summary>
/// The declared-parameter descriptors a worker advertises on <c>POST /api/workers/beat</c>: round-trip
/// through <c>GET /api/workers</c>, and input-hardening for the nested-null shapes an init default does
/// not protect against (<c>jobDescriptors:null</c>, a null element, a null <c>parameters</c>).
/// </summary>
public sealed class WorkerBeatDescriptorTests : IClassFixture<SampleRestApiFactory>
{
    private static readonly Uri BeatUri = new("/api/workers/beat", UriKind.Relative);
    private static readonly Uri ListUri = new("/api/workers", UriKind.Relative);
    private readonly SampleRestApiFactory _factory;

    public WorkerBeatDescriptorTests(SampleRestApiFactory factory) => _factory = factory;

    private static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PostBeat_WithDescriptors_RoundTripsThroughList()
    {
        using var client = _factory.CreateClient();
        const string worker = "descriptor-roundtrip-worker";

        var beat = await client.PostAsync(BeatUri, RawJson($$"""
            {
              "name": "{{worker}}",
              "jobs": ["RemoteJob"],
              "jobDescriptors": [
                { "name": "RemoteJob", "parameters": [
                    { "name": "orderId", "kind": "String", "required": true, "description": "the order" },
                    { "name": "retries", "kind": "Integer", "required": false, "defaultValue": 3 }
                ] }
              ]
            }
            """));
        beat.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var list = await client.GetAsync(ListUri);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var mine = doc.RootElement.EnumerateArray().Single(w => w.GetProperty("name").GetString() == worker);
        var descriptors = mine.GetProperty("jobDescriptors").EnumerateArray().ToList();
        descriptors.Should().ContainSingle();
        descriptors[0].GetProperty("name").GetString().Should().Be("RemoteJob");
        var parameters = descriptors[0].GetProperty("parameters").EnumerateArray().ToList();
        parameters.Should().HaveCount(2);
        parameters[0].GetProperty("name").GetString().Should().Be("orderId");
        parameters[0].GetProperty("kind").GetString().Should().Be("String");
        parameters[0].GetProperty("required").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostBeat_JobDescriptorsExplicitNull_IsAccepted_NoServerError()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(BeatUri, RawJson("""{ "name": "w-null-desc", "jobDescriptors": null }"""));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "an explicit null descriptors array normalizes to empty");
    }

    [Fact]
    public async Task PostBeat_NullDescriptorElement_Returns400_NotServerError()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(BeatUri, RawJson("""{ "name": "w-null-el", "jobDescriptors": [ null ] }"""));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a null descriptor element is rejected, not a 500");
    }

    [Fact]
    public async Task PostBeat_DescriptorParametersExplicitNull_IsAccepted_NoServerError()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(BeatUri,
            RawJson("""{ "name": "w-null-params", "jobDescriptors": [ { "name": "J", "parameters": null } ] }"""));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "a null parameters list normalizes to empty (not a 500)");
    }

    [Fact]
    public async Task PostBeat_BlankDescriptorName_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(BeatUri,
            RawJson("""{ "name": "w-blank-desc", "jobDescriptors": [ { "name": "  ", "parameters": [] } ] }"""));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("JobDescriptors");
    }
}
