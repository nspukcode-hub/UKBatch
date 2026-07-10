using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Parity;

/// <summary>
/// Shared behavioral-parity suite for <see cref="IBatchRunStore"/>. The IDENTICAL assertions run against a
/// store supplied by each derived fixture (in-memory, SQLite, PostgreSQL), so all three passing the same
/// assertions is the proof the EF run store is a behavioral drop-in for the in-memory one — the contract
/// the "swap storage, code unchanged" promise rests on.
/// </summary>
/// <remarks>
/// <para>The single LSP risk here is the status filter: <see cref="BatchRun.Status"/> is a NULLABLE mapped
/// enum-string column, and the <c>Statuses.Contains(e.Status.Value)</c> predicate must translate on BOTH
/// Npgsql and SQLite. The status-filtered query below exercises exactly that translation (the Postgres
/// subclass is the one that proves it on the real provider).</para>
/// <para>Ordering parity uses lowercase-ASCII ids so the id tiebreak stays inside the collation-agreement
/// set (PostgreSQL <c>en_US</c> + SQLite <c>BINARY</c> + Ordinal all agree on <c>[0-9a-f]</c>) — the same
/// rule the job-store parity suite relies on.</para>
/// </remarks>
public abstract class BatchRunStoreParityTestBase : IAsyncLifetime
{
    private protected static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The deterministic clock the stores consume (shared so stamping matches across providers).</summary>
    protected FakeTimeProvider Clock { get; private set; } = default!;

    /// <summary>The store under parity test, supplied by the derived provider fixture.</summary>
    protected IBatchRunStore Store { get; private set; } = default!;

    /// <summary>Builds a fresh, empty run store over this provider with the supplied (shared) clock.</summary>
    protected abstract Task<IBatchRunStore> CreateStoreAsync(FakeTimeProvider clock);

    /// <summary>Tears down provider resources (DB connection / container database). Default no-op (in-memory).</summary>
    protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        Clock = new FakeTimeProvider(T0);
        Store = await CreateStoreAsync(Clock);
    }

    public async Task DisposeAsync() => await DisposeStoreAsync();

    private async Task SeedAsync(params BatchRun[] runs)
    {
        foreach (var r in runs)
        {
            await Store.CreateAsync(r, CancellationToken.None);
        }
    }

    // ===== create / get round-trip =====

    [Fact]
    public async Task CreateThenGet_RoundTripsEveryField_IncludingNullStatusWhileRunning()
    {
        await SeedAsync(TestData.BatchRun(
            "r1",
            batchDefinitionId: "def-A",
            batchName: "Invoices",
            status: null,
            triggeredBy: "user@x",
            startedAtUtc: T0,
            stepCount: 5));

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;

        fetched.BatchId.Should().Be("r1");
        fetched.BatchDefinitionId.Should().Be("def-A");
        fetched.BatchName.Should().Be("Invoices");
        fetched.Status.Should().BeNull("a freshly created run is in-progress (null status)");
        fetched.TriggeredBy.Should().Be("user@x");
        fetched.StartedAtUtc.Should().Be(T0);
        fetched.CompletedAtUtc.Should().BeNull();
        fetched.StepCount.Should().Be(5);
        fetched.Total.Should().Be(0);
        fetched.Succeeded.Should().Be(0);
        fetched.Failed.Should().Be(0);
        fetched.Cancelled.Should().Be(0);
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        (await Store.GetAsync("nope", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Create_DuplicateId_ThrowsInvalidOperation_WithParityMessage()
    {
        await SeedAsync(TestData.BatchRun("dup"));

        var act = async () => await Store.CreateAsync(TestData.BatchRun("dup"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*", "the EF primary-key collision message mirrors the in-memory store verbatim");
    }

    // ===== complete: terminal write (incl. status enum + DateTimeOffset round-trip) =====

    [Fact]
    public async Task CompleteAsync_StampsTerminalStatusCountsAndCompletedAt_RoundTrips()
    {
        await SeedAsync(TestData.BatchRun("r1", stepCount: 3));

        await Store.CompleteAsync("r1", JobStatus.Failed, new BatchRunCounts(3, 1, 2, 0), T0.AddMinutes(5), CancellationToken.None);

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        fetched.Status.Should().Be(JobStatus.Failed, "the terminal status round-trips (enum-as-string on EF)");
        fetched.Total.Should().Be(3);
        fetched.Succeeded.Should().Be(1);
        fetched.Failed.Should().Be(2);
        fetched.Cancelled.Should().Be(0);
        fetched.CompletedAtUtc.Should().Be(T0.AddMinutes(5), "the completion timestamp round-trips (SQLite ISO-8601 converter parity)");
        fetched.StepCount.Should().Be(3, "completion must not overwrite the create-time topology count");
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task CompleteAsync_EachTerminalStatus_RoundTripsAsName(JobStatus terminal)
    {
        await SeedAsync(TestData.BatchRun($"r-{terminal}"));

        await Store.CompleteAsync($"r-{terminal}", terminal, new BatchRunCounts(1, 1, 0, 0), T0.AddMinutes(1), CancellationToken.None);

        var fetched = (await Store.GetAsync($"r-{terminal}", CancellationToken.None))!;
        fetched.Status.Should().Be(terminal);
    }

    [Fact]
    public async Task CompleteAsync_AbsentId_IsNoOp_DoesNotThrow()
    {
        var act = async () => await Store.CompleteAsync(
            "ghost", JobStatus.Failed, new BatchRunCounts(1, 0, 1, 0), T0, CancellationToken.None);

        await act.Should().NotThrowAsync("completion on a missing row must be a no-op on every provider");
        (await Store.GetAsync("ghost", CancellationToken.None)).Should().BeNull("the absent id must not be inserted");
    }

    // ===== update cursor: resume marker round-trip =====

    [Fact]
    public async Task CreatedRun_CurrentStepIndex_DefaultsToNull()
    {
        await SeedAsync(TestData.BatchRun("r1", stepCount: 3));

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        fetched.CurrentStepIndex.Should().BeNull("a freshly created run has no cursor recorded yet");
    }

    [Fact]
    public async Task UpdateCursorAsync_SetsCursor_AndPreservesOtherFields()
    {
        await SeedAsync(TestData.BatchRun("r1", batchDefinitionId: "def-A", batchName: "Invoices", stepCount: 3));

        await Store.UpdateCursorAsync("r1", 2, CancellationToken.None);

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        fetched.CurrentStepIndex.Should().Be(2, "the resume cursor round-trips on every provider");
        fetched.Status.Should().BeNull("advancing the cursor must not terminate the run");
        fetched.BatchDefinitionId.Should().Be("def-A", "a cursor write must not disturb create-time fields");
        fetched.BatchName.Should().Be("Invoices");
        fetched.StepCount.Should().Be(3);
        fetched.Total.Should().Be(0);
    }

    [Fact]
    public async Task UpdateCursorAsync_OverwritesPreviousCursor_LastWriteWins()
    {
        await SeedAsync(TestData.BatchRun("r1", stepCount: 5, currentStepIndex: 1));

        await Store.UpdateCursorAsync("r1", 4, CancellationToken.None);

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        fetched.CurrentStepIndex.Should().Be(4);
    }

    [Fact]
    public async Task UpdateCursorAsync_AbsentId_IsNoOp_DoesNotThrow()
    {
        var act = async () => await Store.UpdateCursorAsync("ghost", 1, CancellationToken.None);

        await act.Should().NotThrowAsync("a cursor write on a missing row must be a no-op on every provider");
        (await Store.GetAsync("ghost", CancellationToken.None)).Should().BeNull("the absent id must not be inserted");
    }

    // ===== update compensation cursor: reverse-unwind marker round-trip =====

    [Fact]
    public async Task CreatedRun_CompensationCursorAndRetryLink_DefaultToNull()
    {
        await SeedAsync(TestData.BatchRun("r1", stepCount: 3));

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        fetched.CompensationStepIndex.Should().BeNull("a freshly created run never entered compensation");
        fetched.RetryOfBatchId.Should().BeNull("a normally-triggered run has no retry lineage");
    }

    [Fact]
    public async Task CreateThenGet_RoundTripsCompensationCursorAndRetryOfBatchId()
    {
        await SeedAsync(TestData.BatchRun("r2", stepCount: 4, compensationStepIndex: 2, retryOfBatchId: "r-original"));

        var fetched = (await Store.GetAsync("r2", CancellationToken.None))!;
        fetched.CompensationStepIndex.Should().Be(2, "the unwind cursor set at create time round-trips on every provider");
        fetched.RetryOfBatchId.Should().Be("r-original", "the retry lineage link round-trips on every provider");
    }

    [Fact]
    public async Task UpdateCompensationCursorAsync_SetsOverwritesClearsNull_AbsentNoOp()
    {
        await SeedAsync(TestData.BatchRun("r1", batchDefinitionId: "def-A", batchName: "Invoices", stepCount: 3, currentStepIndex: 2));

        // Set: the initial unwind marker (the failed step's index) round-trips.
        await Store.UpdateCompensationCursorAsync("r1", 2, CancellationToken.None);
        var afterSet = (await Store.GetAsync("r1", CancellationToken.None))!;
        afterSet.CompensationStepIndex.Should().Be(2, "the unwind cursor round-trips on every provider");
        afterSet.Status.Should().BeNull("writing the unwind cursor must not terminate the run");
        afterSet.CurrentStepIndex.Should().Be(2, "the unwind cursor must not disturb the forward cursor");
        afterSet.BatchDefinitionId.Should().Be("def-A", "an unwind-cursor write must not disturb create-time fields");
        afterSet.BatchName.Should().Be("Invoices");
        afterSet.StepCount.Should().Be(3);

        // Overwrite: the cursor descends as each compensator finishes; last write wins.
        await Store.UpdateCompensationCursorAsync("r1", 1, CancellationToken.None);
        (await Store.GetAsync("r1", CancellationToken.None))!.CompensationStepIndex.Should().Be(1);

        // Clear to null: an explicit restart policy abandons the unwind.
        await Store.UpdateCompensationCursorAsync("r1", null, CancellationToken.None);
        (await Store.GetAsync("r1", CancellationToken.None))!.CompensationStepIndex.Should().BeNull(
            "a null write clears the unwind cursor on every provider");

        // Absent id: no-op, no insert (mirrors the forward-cursor contract).
        var act = async () => await Store.UpdateCompensationCursorAsync("ghost", 1, CancellationToken.None);
        await act.Should().NotThrowAsync("an unwind-cursor write on a missing row must be a no-op on every provider");
        (await Store.GetAsync("ghost", CancellationToken.None)).Should().BeNull("the absent id must not be inserted");
    }

    // ===== update forwarded state: durable resume payload round-trip =====

    /// <summary>
    /// Normalizes a SCALAR forwarded-state value to its string form regardless of provider representation
    /// (CLR value on in-memory, <see cref="JsonElement"/> on EF) — the documented JSON-equality axis.
    /// </summary>
    private static string? StateString(BatchRun run, string key)
    {
        if (run.ForwardedState is null || !run.ForwardedState.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }
        return v is JsonElement je ? je.ToString() : v.ToString();
    }

    /// <summary>
    /// Coerces a NESTED forwarded-state value (itself a dictionary) into a normalized
    /// <c>key -&gt; string</c> map regardless of provider: it is a live dictionary in-memory but a JSON object
    /// on EF. This is the same shape the runtime reads forwarded state back as, so the assertion compares the
    /// product's actual equality axis rather than a provider-specific representation.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> NestedState(BatchRun run, string key)
    {
        run.ForwardedState.Should().NotBeNull();
        run.ForwardedState!.TryGetValue(key, out var raw).Should().BeTrue($"forwarded state must contain '{key}'");
        return raw switch
        {
            IReadOnlyDictionary<string, object?> dict =>
                dict.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString(), StringComparer.Ordinal),
            JsonElement { ValueKind: JsonValueKind.Object } element =>
                element.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString(),
                    StringComparer.Ordinal),
            _ => throw new InvalidOperationException($"unexpected nested forwarded-state shape: {raw?.GetType().Name ?? "null"}"),
        };
    }

    [Fact]
    public async Task FreshlyCreated_ForwardedState_DefaultsToNull_NoSpuriousWrite()
    {
        await SeedAsync(TestData.BatchRun("r1", stepCount: 2));

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        fetched.ForwardedState.Should().BeNull("a freshly created run has no forwarded state recorded yet");
    }

    [Fact]
    public async Task UpdateForwardedStateAsync_RoundTripsVerbatim_AndPreservesOtherFields()
    {
        // The resume payload (batch-initial parameters + accumulated outputs under reserved keys) must
        // survive the round-trip on every provider, and a forwarded-state write must not disturb the
        // create-time fields or terminate the run. Compared by NORMALIZED string form (object? → JsonElement
        // on EF). A nested dictionary value comes back as a JSON object on EF, so we compare its serialized
        // text rather than CLR identity.
        await SeedAsync(TestData.BatchRun("r1", batchDefinitionId: "def-A", batchName: "Invoices", stepCount: 3));

        var state = new Dictionary<string, object?>
        {
            ["ukbatch.initialParameters"] = new Dictionary<string, object?> { ["region"] = "EU" },
            ["ukbatch.forwardedOutputs"] = new Dictionary<string, object?> { ["orderId"] = 5 },
        };

        await Store.UpdateForwardedStateAsync("r1", state, CancellationToken.None);

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        fetched.ForwardedState.Should().NotBeNull();
        fetched.ForwardedState!.Should().ContainKey("ukbatch.initialParameters");
        fetched.ForwardedState!.Should().ContainKey("ukbatch.forwardedOutputs");
        // The reserved-key values are nested dictionaries; compare their normalized entries (provider-agnostic).
        NestedState(fetched, "ukbatch.initialParameters")["region"].Should().Be("EU");
        NestedState(fetched, "ukbatch.forwardedOutputs")["orderId"].Should().Be("5");

        // Create-time fields untouched; the run is still in progress.
        fetched.Status.Should().BeNull("writing forwarded state must not terminate the run");
        fetched.BatchDefinitionId.Should().Be("def-A");
        fetched.BatchName.Should().Be("Invoices");
        fetched.StepCount.Should().Be(3);
        fetched.CurrentStepIndex.Should().BeNull("a forwarded-state write must not disturb the cursor");
    }

    [Fact]
    public async Task UpdateForwardedStateAsync_OverwritesPrevious_LastWriteWins()
    {
        await SeedAsync(TestData.BatchRun("r1", stepCount: 3));

        await Store.UpdateForwardedStateAsync(
            "r1", new Dictionary<string, object?> { ["k"] = "first" }, CancellationToken.None);
        await Store.UpdateForwardedStateAsync(
            "r1", new Dictionary<string, object?> { ["k"] = "second" }, CancellationToken.None);

        var fetched = (await Store.GetAsync("r1", CancellationToken.None))!;
        StateString(fetched, "k").Should().Be("second", "the latest forwarded-state write wins");
    }

    [Fact]
    public async Task UpdateForwardedStateAsync_AbsentId_IsNoOp_DoesNotThrow()
    {
        var act = async () => await Store.UpdateForwardedStateAsync(
            "ghost", new Dictionary<string, object?> { ["k"] = 1 }, CancellationToken.None);

        await act.Should().NotThrowAsync("a forwarded-state write on a missing row must be a no-op on every provider");
        (await Store.GetAsync("ghost", CancellationToken.None)).Should().BeNull("the absent id must not be inserted");
    }

    // ===== query: filters =====

    [Fact]
    public async Task Query_FilterByBatchDefinitionId_ReturnsOnlyMatchingDefinition()
    {
        await SeedAsync(
            TestData.BatchRun("a1", batchDefinitionId: "def-A"),
            TestData.BatchRun("a2", batchDefinitionId: "def-A"),
            TestData.BatchRun("b1", batchDefinitionId: "def-B"));

        var result = await Store.QueryAsync(new BatchRunQuery { BatchDefinitionId = "def-A", Limit = 100 }, CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "a1", "a2" });
    }

    [Fact]
    public async Task Query_StatusesFilter_IncludeRunningFalse_ReturnsOnlyMatchingTerminalRuns()
    {
        // The translation-critical query: a NULLABLE enum-string column filtered by a status set, with
        // running runs excluded. This must translate to SQL on Npgsql AND SQLite (the single LSP risk).
        await SeedAsync(
            TestData.BatchRun("failed", status: JobStatus.Failed),
            TestData.BatchRun("completed", status: JobStatus.Completed),
            TestData.BatchRun("running", status: null));

        var result = await Store.QueryAsync(
            new BatchRunQuery { Statuses = new[] { JobStatus.Failed }, IncludeRunning = false, Limit = 100 },
            CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "failed" },
            "a status filter returns only the matching terminal runs; a null-status run cannot be in any set");
    }

    [Fact]
    public async Task Query_StatusesFilter_IncludeRunningTrue_AlsoReturnsRunningRuns()
    {
        await SeedAsync(
            TestData.BatchRun("failed", status: JobStatus.Failed),
            TestData.BatchRun("completed", status: JobStatus.Completed),
            TestData.BatchRun("running", status: null));

        var result = await Store.QueryAsync(
            new BatchRunQuery { Statuses = new[] { JobStatus.Failed }, IncludeRunning = true, Limit = 100 },
            CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "failed", "running" },
            "IncludeRunning=true surfaces the null-status run alongside the matching terminal runs");
    }

    [Fact]
    public async Task Query_IncludeRunningFalse_NoStatusFilter_ExcludesRunningRuns()
    {
        await SeedAsync(
            TestData.BatchRun("running", status: null),
            TestData.BatchRun("done", status: JobStatus.Completed));

        var result = await Store.QueryAsync(new BatchRunQuery { IncludeRunning = false, Limit = 100 }, CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "done" });
    }

    [Fact]
    public async Task Query_IncludeRunningTrue_NoStatusFilter_ReturnsEveryRun()
    {
        await SeedAsync(
            TestData.BatchRun("running", status: null),
            TestData.BatchRun("done", status: JobStatus.Completed));

        var result = await Store.QueryAsync(new BatchRunQuery { IncludeRunning = true, Limit = 100 }, CancellationToken.None);

        result.Select(r => r.BatchId).Should().BeEquivalentTo(new[] { "running", "done" });
    }

    // ===== query: ordering / paging =====

    [Fact]
    public async Task Query_DescendingByStartedAt_NewestFirst()
    {
        await SeedAsync(
            TestData.BatchRun("old", startedAtUtc: T0),
            TestData.BatchRun("mid", startedAtUtc: T0.AddHours(1)),
            TestData.BatchRun("new", startedAtUtc: T0.AddHours(2)));

        var result = await Store.QueryAsync(new BatchRunQuery { DescendingByStartedAt = true, Limit = 100 }, CancellationToken.None);
        result.Select(r => r.BatchId).Should().Equal("new", "mid", "old");
    }

    [Fact]
    public async Task Query_IdTiebreak_OnEqualStartedAt()
    {
        // Equal timestamps → tie broken by id. Lowercase-ASCII ids stay inside the collation-agreement set.
        await SeedAsync(
            TestData.BatchRun("c", startedAtUtc: T0),
            TestData.BatchRun("a", startedAtUtc: T0),
            TestData.BatchRun("b", startedAtUtc: T0));

        var asc = await Store.QueryAsync(new BatchRunQuery { DescendingByStartedAt = false, Limit = 100 }, CancellationToken.None);
        asc.Select(r => r.BatchId).Should().Equal("a", "b", "c");

        var desc = await Store.QueryAsync(new BatchRunQuery { DescendingByStartedAt = true, Limit = 100 }, CancellationToken.None);
        // Descending-by-time keeps the id tiebreak descending too (ThenByDescending after OrderByDescending).
        desc.Select(r => r.BatchId).Should().Equal("c", "b", "a");
    }

    [Fact]
    public async Task Query_Paging_OffsetAndLimit()
    {
        for (var i = 0; i < 10; i++)
        {
            await Store.CreateAsync(TestData.BatchRun($"r{i:D2}", startedAtUtc: T0.AddMinutes(i)), CancellationToken.None);
        }

        var page = await Store.QueryAsync(
            new BatchRunQuery { Offset = 2, Limit = 3, DescendingByStartedAt = false }, CancellationToken.None);
        page.Select(r => r.BatchId).Should().Equal("r02", "r03", "r04");
    }

    [Fact]
    public async Task Count_RespectsFilter_IgnoresPaging()
    {
        await SeedAsync(
            TestData.BatchRun("a1", batchDefinitionId: "def-A"),
            TestData.BatchRun("a2", batchDefinitionId: "def-A"),
            TestData.BatchRun("b1", batchDefinitionId: "def-B"));

        var count = await Store.CountAsync(
            new BatchRunQuery { BatchDefinitionId = "def-A", Offset = 1, Limit = 1 }, CancellationToken.None);

        count.Should().Be(2, "Count applies the filter but ignores Offset/Limit");
    }
}
