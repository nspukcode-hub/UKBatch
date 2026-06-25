using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UKBatch.Abstractions.Storage;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

// <summary> — <c>/batches</c> surface tests.</summary>
public sealed class BatchesEndpointTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public BatchesEndpointTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetBatches_ListsAcrossSources()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/batches", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(b => b.GetProperty("name").GetString())
            .ToList();
        names.Should().Contain("invoice-pipeline");
    }

    [Fact]
    public async Task GetBatchByName_ReturnsCodeBatch()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/batches/by-name/invoice-pipeline", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("source").GetString().Should().Be("Code");
    }

    [Fact]
    public async Task GetBatchByName_UnknownName_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/batches/by-name/does-not-exist", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:batch-not-found");
    }

    [Fact]
    public async Task RunBatchByName_Returns202_WithBatchId()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/invoice-pipeline/run", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { initialParameters = new Dictionary<string, object?>() }));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("batchId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunByName_UnknownName_Returns404_BeforeDispatch()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/no-such-batch/run", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RunByName_SourceFilterCode_ResolvesCodeBatch()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/invoice-pipeline/run?source=Code", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RunByName_SourceFilterDashboard_OnCodeBatchReturns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/invoice-pipeline/run?source=Dashboard", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateBatch_CodeSource_Returns400()
    {
        using var client = _factory.CreateClient();
        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = "my-batch",
            source = "Code",
            steps = Array.Empty<object>(),
            failurePolicy = "StopOnFailure",
        });
        var response = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateBatch_Dashboard_Returns201_ThenDuplicateReturns409()
    {
        using var client = _factory.CreateClient();
        var uniqueName = $"dashboard-batch-{Guid.NewGuid():N}";
        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = uniqueName,
            source = "Dashboard",
            steps = new[]
            {
                new
                {
                    stepId = "s1",
                    order = 0,
                    stepType = "Job",
                    job = new { jobName = "Sample.RestApi.Jobs.InvoiceGenerationJob" },
                },
            },
            failurePolicy = "StopOnFailure",
        });
        var first = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Duplicate with the same name within the same source -> 409.
        var dupPayload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = uniqueName,
            source = "Dashboard",
            steps = new[]
            {
                new
                {
                    stepId = "s1",
                    order = 0,
                    stepType = "Job",
                    job = new { jobName = "Sample.RestApi.Jobs.InvoiceGenerationJob" },
                },
            },
            failurePolicy = "StopOnFailure",
        });
        var dup = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), dupPayload);
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteBatch_AbsentId_Returns204_Idempotent()
    {
        using var client = _factory.CreateClient();
        var response = await client.DeleteAsync(new Uri("/api/batches/by-id/nonexistent-id", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteBatch_CodeSource_Returns400()
    {
        // Resolve the Code batch's id via the catalog.
        using var client = _factory.CreateClient();
        var getResp = await client.GetAsync(new Uri("/api/batches/by-name/invoice-pipeline", UriKind.Relative));
        var json = await getResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var codeId = doc.RootElement.GetProperty("id").GetString();
        codeId.Should().NotBeNullOrWhiteSpace();
        var delResp = await client.DeleteAsync(new Uri($"/api/batches/by-id/{codeId}", UriKind.Relative));
        delResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BatchRunIdPath_DoesNotClashWithByIdPath()
    {
        // Verify both routes resolve distinctly:
        // /batches/by-id/{id} -> GetBatchById
        // /batches/{batchRunId}/status -> GetBatchRunStatus
        using var client = _factory.CreateClient();
        var byIdResp = await client.GetAsync(new Uri("/api/batches/by-id/nope", UriKind.Relative));
        byIdResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // RUN-keyed: empty list, NOT 404.
        var runResp = await client.GetAsync(new Uri("/api/batches/some-run-id/status", UriKind.Relative));
        runResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PauseThenResume_TogglesScheduleEnabled()
    {
        using var client = _factory.CreateClient();
        var id = await CreateScheduledBatchAsync(client, $"sched-batch-{Guid.NewGuid():N}");
        (await GetScheduleEnabledAsync(client, id)).Should().BeTrue("a new scheduled batch defaults to enabled");

        var pause = await client.PostAsync(new Uri($"/api/batches/by-id/{id}/pause", UriKind.Relative), Empty());
        pause.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetScheduleEnabledAsync(client, id)).Should().BeFalse("pause flips ScheduleEnabled to false");

        var resume = await client.PostAsync(new Uri($"/api/batches/by-id/{id}/resume", UriKind.Relative), Empty());
        resume.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetScheduleEnabledAsync(client, id)).Should().BeTrue("resume flips ScheduleEnabled back to true");
    }

    [Fact]
    public async Task Pause_IsIdempotent_SecondCallReturns204()
    {
        using var client = _factory.CreateClient();
        var id = await CreateScheduledBatchAsync(client, $"sched-batch-{Guid.NewGuid():N}");
        (await client.PostAsync(new Uri($"/api/batches/by-id/{id}/pause", UriKind.Relative), Empty())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsync(new Uri($"/api/batches/by-id/{id}/pause", UriKind.Relative), Empty())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetScheduleEnabledAsync(client, id)).Should().BeFalse("a repeated pause is a no-op and stays paused");
    }

    [Fact]
    public async Task Pause_UnknownId_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(new Uri($"/api/batches/by-id/no-such-{Guid.NewGuid():N}/pause", UriKind.Relative), Empty());
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ukbatch:batch-definition-not-found");
    }

    [Fact]
    public async Task Pause_CodeSource_Returns400()
    {
        using var client = _factory.CreateClient();
        var getResp = await client.GetAsync(new Uri("/api/batches/by-name/invoice-pipeline", UriKind.Relative));
        using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        var codeId = doc.RootElement.GetProperty("id").GetString();
        var response = await client.PostAsync(new Uri($"/api/batches/by-id/{codeId}/pause", UriKind.Relative), Empty());
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateBatch_ScheduleEnabledFalse_PersistsPaused()
    {
        using var client = _factory.CreateClient();
        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = $"paused-batch-{Guid.NewGuid():N}",
            source = "Dashboard",
            schedule = "0 0 * * * *",
            scheduleEnabled = false,
            steps = OneJobStep(),
            failurePolicy = "StopOnFailure",
        });
        var created = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("scheduleEnabled").GetBoolean().Should().BeFalse("a batch created with scheduleEnabled=false starts paused");
    }

    [Fact]
    public async Task Resume_CatchUpEnabledBatch_AdvancesWatermarkOnce()
    {
        // The watermark advance is what stops a restart-after-resume from replaying the deliberately-paused
        // gap. The in-memory default has no schedule-state store, so this injects a recording store to prove
        // resume calls RecordFiredAsync exactly once (and pause does not).
        var recording = new RecordingScheduleStateStore();
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IScheduleStateStore>();   // robust if the sample ever defaults to a real store
                s.AddSingleton<IScheduleStateStore>(recording);
            }));
        using var client = factory.CreateClient();

        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = $"catchup-batch-{Guid.NewGuid():N}",
            source = "Dashboard",
            schedule = "0 0 * * * *",
            scheduleCatchUpWindow = "01:00:00",
            steps = OneJobStep(),
            failurePolicy = "StopOnFailure",
        });
        var created = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;

        await client.PostAsync(new Uri($"/api/batches/by-id/{id}/pause", UriKind.Relative), Empty());
        recording.Recorded.Should().BeEmpty("pausing does not advance the watermark");

        await client.PostAsync(new Uri($"/api/batches/by-id/{id}/resume", UriKind.Relative), Empty());
        recording.Recorded.Should().ContainSingle("resume advances the watermark once for a catch-up-enabled batch")
            .Which.Should().Be(id);
    }

    [Fact]
    public async Task Resume_NonCatchUpBatch_DoesNotAdvanceWatermark()
    {
        // A scheduled batch WITHOUT a catch-up window has no watermark, so resume must not call RecordFiredAsync.
        var recording = new RecordingScheduleStateStore();
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IScheduleStateStore>();
                s.AddSingleton<IScheduleStateStore>(recording);
            }));
        using var client = factory.CreateClient();

        var id = await CreateScheduledBatchAsync(client, $"no-catchup-{Guid.NewGuid():N}");   // schedule, no catch-up window
        await client.PostAsync(new Uri($"/api/batches/by-id/{id}/pause", UriKind.Relative), Empty());
        await client.PostAsync(new Uri($"/api/batches/by-id/{id}/resume", UriKind.Relative), Empty());

        recording.Recorded.Should().BeEmpty("a batch with no catch-up window has no watermark to advance");
    }

    [Fact]
    public async Task Resume_WhenWatermarkWriteFails_StillSucceeds()
    {
        // A schedule-store fault while advancing the watermark must NOT fail a resume that already committed —
        // the advance is best-effort (worst case a restart replays one paused-window occurrence).
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IScheduleStateStore>();
                s.AddSingleton<IScheduleStateStore>(new ThrowingScheduleStateStore());
            }));
        using var client = factory.CreateClient();

        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = $"catchup-fail-{Guid.NewGuid():N}",
            source = "Dashboard",
            schedule = "0 0 * * * *",
            scheduleCatchUpWindow = "01:00:00",
            steps = OneJobStep(),
            failurePolicy = "StopOnFailure",
        });
        var created = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        await client.PostAsync(new Uri($"/api/batches/by-id/{id}/pause", UriKind.Relative), Empty());

        var resume = await client.PostAsync(new Uri($"/api/batches/by-id/{id}/resume", UriKind.Relative), Empty());
        resume.StatusCode.Should().Be(HttpStatusCode.NoContent, "a watermark-write fault must not fail a committed resume");
        (await GetScheduleEnabledAsync(client, id)).Should().BeTrue("the resume persisted despite the watermark fault");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private static StringContent Empty() => new(string.Empty);

    private static object[] OneJobStep() =>
    [
        new { stepId = "s1", order = 0, stepType = "Job", job = new { jobName = "Sample.RestApi.Jobs.InvoiceGenerationJob" } },
    ];

    private static async Task<string> CreateScheduledBatchAsync(HttpClient client, string name)
    {
        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name,
            source = "Dashboard",
            schedule = "0 0 * * * *",
            steps = OneJobStep(),
            failurePolicy = "StopOnFailure",
        });
        var created = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<bool> GetScheduleEnabledAsync(HttpClient client, string id)
    {
        var resp = await client.GetAsync(new Uri($"/api/batches/by-id/{id}", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("scheduleEnabled").GetBoolean();
    }

    /// <summary>Records every RecordFiredAsync call to prove the resume watermark advance fires.</summary>
    private sealed class RecordingScheduleStateStore : IScheduleStateStore
    {
        private readonly object _lock = new();
        private readonly List<string> _recorded = [];
        public IReadOnlyList<string> Recorded { get { lock (_lock) { return _recorded.ToList(); } } }

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(
                new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));

        public Task RecordFiredAsync(string batchDefinitionId, DateTimeOffset occurrenceUtc, CancellationToken cancellationToken)
        {
            lock (_lock) { _recorded.Add(batchDefinitionId); }
            return Task.CompletedTask;
        }
    }

    /// <summary>A schedule-state store whose RecordFiredAsync faults — proves resume survives a watermark write failure.</summary>
    private sealed class ThrowingScheduleStateStore : IScheduleStateStore
    {
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(
                new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));

        public Task RecordFiredAsync(string batchDefinitionId, DateTimeOffset occurrenceUtc, CancellationToken cancellationToken)
            => throw new InvalidOperationException("schedule store unavailable");
    }
}
