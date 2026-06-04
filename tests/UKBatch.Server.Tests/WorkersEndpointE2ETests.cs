using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Server.Tests.Common;
using Xunit;

namespace UKBatch.Server.Tests;

/// <summary>
/// The <c>/api/workers/*</c> surface exercised end-to-end over the
/// <c>UKBatch.Server</c> WAF: a valid beat is accepted (202) and surfaces in the list with a STRING
/// enum status (<c>"Online"</c>, not <c>0</c>) + the live <c>online:true</c> flag + the advertised
/// jobs/tags; a blank name and an over-cap jobs list are both rejected (400 ValidationProblem).
/// </summary>
public sealed class WorkersEndpointE2ETests
{
    private static readonly Uri BeatUri = new("/api/workers/beat", UriKind.Relative);
    private static readonly Uri ListUri = new("/api/workers", UriKind.Relative);
    private static readonly string[] OneJob = ["GenerateInvoice"];
    private static readonly string[] OneTag = ["billing"];
    private static readonly string[] TwoJobs = ["GenerateInvoice", "ShipOrder"];
    private static readonly string[] TwoTags = ["billing", "eu-west"];
    private static readonly string[] NoStrings = [];

    private static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PostBeat_ValidPayload_Returns202()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(BeatUri, new
        {
            name = "invoicing",
            jobs = OneJob,
            tags = OneTag,
            status = "Online",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "a valid beat ingests with 202 Accepted");
    }

    [Fact]
    public async Task PostBeatThenList_ReturnsWorker_WithStringEnumStatusAndOnlineTrue()
    {
        // Unique name so this test is isolated from any other beat in the shared registry singleton.
        var workerName = "e2e-online-" + Guid.NewGuid().ToString("N");
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var beat = await client.PostAsJsonAsync(BeatUri, new
        {
            name = workerName,
            jobs = TwoJobs,
            tags = TwoTags,
            status = "Online",
        });
        beat.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var listJson = await client.GetStringAsync(ListUri);
        using var doc = JsonDocument.Parse(listJson);

        var row = doc.RootElement.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("name").GetString() == workerName);
        row.ValueKind.Should().Be(JsonValueKind.Object, "the just-beaten worker appears in the list");

        // STRING enum on the wire (JsonStringEnumConverter both ends) — NOT the integer 0.
        row.GetProperty("status").ValueKind.Should().Be(JsonValueKind.String);
        row.GetProperty("status").GetString().Should().Be("Online",
            "WorkerStatus serializes as a string ('Online'), never the underlying integer");

        row.GetProperty("online").GetBoolean().Should().BeTrue(
            "a fresh Online beat is within the TTL → online:true");

        row.GetProperty("jobs").EnumerateArray().Select(j => j.GetString())
            .Should().BeEquivalentTo("GenerateInvoice", "ShipOrder");
        row.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
            .Should().BeEquivalentTo("billing", "eu-west");
    }

    [Fact]
    public async Task PostBeat_BlankName_Returns400ValidationProblem()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsync(BeatUri, RawJson("""{ "name": "  ", "jobs": [], "tags": [], "status": "Online" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a blank worker name is rejected with a 400 ValidationProblem");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Name", "the validation problem names the offending field");
    }

    [Fact]
    public async Task PostBeat_JobsOverCap_Returns400()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        // 1001 jobs > the 1000 defensive cap.
        var jobs = string.Join(",", Enumerable.Range(0, 1001).Select(i => $"\"job{i}\""));
        var payload = $$"""{ "name": "floody", "jobs": [{{jobs}}], "tags": [], "status": "Online" }""";

        var resp = await client.PostAsync(BeatUri, RawJson(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a misbehaving worker advertising > 1000 jobs is rejected (registry-balloon guard)");
    }

    [Fact]
    public async Task PostBeat_OfflineStatus_ListedWithOnlineFalse()
    {
        var workerName = "e2e-offline-" + Guid.NewGuid().ToString("N");
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var beat = await client.PostAsJsonAsync(BeatUri, new
        {
            name = workerName,
            jobs = NoStrings,
            tags = NoStrings,
            status = "Offline",
        });
        beat.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var listJson = await client.GetStringAsync(ListUri);
        using var doc = JsonDocument.Parse(listJson);
        var row = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == workerName);

        row.GetProperty("status").GetString().Should().Be("Offline");
        row.GetProperty("online").GetBoolean().Should().BeFalse(
            "an explicit Offline beat marks the row offline immediately, even within the TTL");
    }
}
