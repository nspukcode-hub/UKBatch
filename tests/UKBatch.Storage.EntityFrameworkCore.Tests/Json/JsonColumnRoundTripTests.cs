using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore.Json;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Json;

/// <summary>
/// JSON-column round-trip fidelity through the real SQLite converters: Parameters dict, Steps list,
/// nested ParallelGroup, ApprovalGateConfig, and <see cref="BatchStep.Metadata"/> verbatim (the
/// forward-compat invariant). Plus the enum-as-NAME round-trip and the proof that the
/// <c>ReferenceEquals</c> fast-path does NOT drop a genuine change.
/// </summary>
public sealed class JsonColumnRoundTripTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfJobStore _jobStore = default!;
    private EfBatchDefinitionStore _batchStore = default!;
    private EfApprovalGateStore _gateStore = default!;

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _jobStore = new EfJobStore(_harness.Factory, new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance),
            _harness.Clock, NullLogger<EfJobStore>.Instance);
        _batchStore = new EfBatchDefinitionStore(_harness.Factory);
        _gateStore = new EfApprovalGateStore(_harness.Factory);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Parameters_Dictionary_RoundTrips()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["count"] = 42,
            ["name"] = "alpha",
            ["enabled"] = true,
            ["ratio"] = 3.14,
        };
        await _jobStore.InsertAsync(TestData.Execution("e1", parameters: parameters), CancellationToken.None);

        var fetched = await _jobStore.GetAsync("e1", CancellationToken.None);
        fetched!.Parameters.Should().ContainKeys("count", "name", "enabled", "ratio");
        // object? values deserialize as JsonElement (documented "raw dict" contract) — compare by serialized form.
        JsonSerializer.Serialize(fetched.Parameters, JsonColumn.Opts)
            .Should().Be(JsonSerializer.Serialize(parameters, JsonColumn.Opts));
    }

    [Fact]
    public async Task Parameters_Empty_RoundTrips()
    {
        await _jobStore.InsertAsync(TestData.Execution("e1", parameters: new Dictionary<string, object?>()), CancellationToken.None);
        var fetched = await _jobStore.GetAsync("e1", CancellationToken.None);
        fetched!.Parameters.Should().BeEmpty();
    }

    [Fact]
    public async Task Parameters_Unicode_RoundTrips()
    {
        var parameters = new Dictionary<string, object?> { ["müşteri"] = "açıklama 日本語 🎉" };
        await _jobStore.InsertAsync(TestData.Execution("e1", parameters: parameters), CancellationToken.None);

        var fetched = await _jobStore.GetAsync("e1", CancellationToken.None);
        fetched!.Parameters.Should().ContainKey("müşteri");
        JsonSerializer.Serialize(fetched.Parameters, JsonColumn.Opts)
            .Should().Be(JsonSerializer.Serialize(parameters, JsonColumn.Opts));
    }

    [Fact]
    public async Task Steps_SimpleJobList_RoundTrips()
    {
        var steps = new[]
        {
            TestData.JobStep("s1", 0, "Job.One"),
            TestData.JobStep("s2", 1, "Job.Two"),
        };
        await _batchStore.CreateAsync(TestData.BatchDef("def-1", "batch", steps: steps), CancellationToken.None);

        var fetched = await _batchStore.GetAsync("def-1", CancellationToken.None);
        fetched!.Steps.Should().HaveCount(2);
        fetched.Steps[0].Job!.JobName.Should().Be("Job.One");
        fetched.Steps[1].Order.Should().Be(1);
    }

    [Fact]
    public async Task Steps_NestedParallelGroup_RoundTrips()
    {
        var parallel = TestData.ParallelStep("pg", 0, ParallelJoinPolicy.WaitMajority,
            TestData.JobStep("c1", 0, "Child.One"),
            TestData.JobStep("c2", 1, "Child.Two"),
            TestData.JobStep("c3", 2, "Child.Three"));

        await _batchStore.CreateAsync(TestData.BatchDef("def-1", "batch", steps: new[] { parallel }), CancellationToken.None);

        var fetched = await _batchStore.GetAsync("def-1", CancellationToken.None);
        var pg = fetched!.Steps.Single();
        pg.StepType.Should().Be(BatchStepType.ParallelGroup);
        pg.ParallelGroup!.JoinPolicy.Should().Be(ParallelJoinPolicy.WaitMajority);
        pg.ParallelGroup.Steps.Should().HaveCount(3);
        pg.ParallelGroup.Steps.Select(s => s.Job!.JobName).Should().Equal("Child.One", "Child.Two", "Child.Three");
    }

    [Fact]
    public async Task Steps_ApprovalGateConfig_RoundTrips()
    {
        var approval = TestData.ApprovalStep("ag", 0, TestData.GateConfig(
            title: "Release?",
            allowedRoles: new[] { "ops", "release-mgr" },
            timeoutAfter: TimeSpan.FromHours(2),
            onTimeout: ApprovalTimeoutAction.AutoApprove));

        await _batchStore.CreateAsync(TestData.BatchDef("def-1", "batch", steps: new[] { approval }), CancellationToken.None);

        var fetched = await _batchStore.GetAsync("def-1", CancellationToken.None);
        var cfg = fetched!.Steps.Single().Approval!;
        cfg.Title.Should().Be("Release?");
        cfg.AllowedRoles.Should().BeEquivalentTo(new[] { "ops", "release-mgr" });
        cfg.TimeoutAfter.Should().Be(TimeSpan.FromHours(2));
        cfg.OnTimeout.Should().Be(ApprovalTimeoutAction.AutoApprove);
    }

    [Fact]
    public async Task Steps_Metadata_RoundTripsVerbatim_ForwardCompat()
    {
        // BatchStep.Metadata is the v0.2 forward-compat seam — a v0.1 read-write cycle must not destroy it.
        var step = TestData.JobStep("s1", 0, "Job.One") with
        {
            Metadata = new Dictionary<string, object?>
            {
                ["futureStepType"] = "ConditionalBranch",
                ["v2Config"] = "opaque-blob",
            },
        };
        await _batchStore.CreateAsync(TestData.BatchDef("def-1", "batch", steps: new[] { step }), CancellationToken.None);

        var fetched = await _batchStore.GetAsync("def-1", CancellationToken.None);
        var md = fetched!.Steps.Single().Metadata;
        md.Should().NotBeNull();
        md!.Should().ContainKey("futureStepType");
        md.Should().ContainKey("v2Config");
    }

    [Fact]
    public async Task Steps_NullMetadata_RoundTripsAsNull()
    {
        var step = TestData.JobStep("s1", 0, "Job.One");   // Metadata null
        await _batchStore.CreateAsync(TestData.BatchDef("def-1", "batch", steps: new[] { step }), CancellationToken.None);

        var fetched = await _batchStore.GetAsync("def-1", CancellationToken.None);
        fetched!.Steps.Single().Metadata.Should().BeNull();
    }

    [Fact]
    public async Task Steps_EnumsSerializeAsNames_NotIntegers()
    {
        // the JSON blob must encode enums as NAMES so a v0.2 reader of a v0.1 blob is forward-compat.
        var step = TestData.ApprovalStep("ag", 0, TestData.GateConfig(onTimeout: ApprovalTimeoutAction.AutoApprove));
        await _batchStore.CreateAsync(TestData.BatchDef("def-1", "batch", steps: new[] { step }), CancellationToken.None);

        // Inspect the raw stored JSON.
        await using var db = await _harness.NewContextAsync();
        var rawJson = await db.Database
            .SqlQueryRaw<string>("SELECT Steps AS Value FROM BatchDefinitions WHERE Id = 'def-1'")
            .SingleAsync();

        rawJson.Should().Contain("AutoApprove", "OnTimeout enum must serialize as its NAME");
        rawJson.Should().Contain("ApprovalGate", "StepType enum must serialize as its NAME");
        rawJson.Should().NotContain("\"OnTimeout\":1", "enums must NOT serialize as integers");
    }

    [Fact]
    public async Task Update_GenuineStepChange_IsPersisted_FastPathDoesNotDropIt()
    {
        // The ReferenceEquals fast-path must not cause a real change (new reference) to be skipped.
        var created = await _batchStore.CreateAsync(
            TestData.BatchDef("def-1", "batch", steps: new[] { TestData.JobStep("s1", 0, "Original.Job") }),
            CancellationToken.None);

        var modified = created with { Steps = new[] { TestData.JobStep("s1", 0, "Changed.Job") } };
        await _batchStore.UpdateAsync(modified, CancellationToken.None);

        var fetched = await _batchStore.GetAsync("def-1", CancellationToken.None);
        fetched!.Steps.Single().Job!.JobName.Should().Be("Changed.Job",
            "a genuine Steps change (new reference) must be detected by the comparer and persisted");
    }

    [Fact]
    public async Task ApprovalGateConfig_NullDescription_RoundTrips()
    {
        // ApprovalGateConfig.Description is nullable; WhenWritingNull must round-trip null cleanly.
        var gate = TestData.Gate("g1", config: TestData.GateConfig());
        await _gateStore.SaveAsync(gate, CancellationToken.None);

        var fetched = await _gateStore.GetAsync("g1", CancellationToken.None);
        fetched!.Config.Description.Should().BeNull();
        fetched.Config.Title.Should().Be("Confirm");
    }
}
