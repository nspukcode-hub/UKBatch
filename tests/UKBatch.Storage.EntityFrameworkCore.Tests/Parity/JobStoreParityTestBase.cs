using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Parity;

/// <summary>
/// Shared behavioral-parity suite (<c>ProviderParityTests</c>). The IDENTICAL
/// assertions run against an <see cref="IJobStoreInternal"/> supplied by each derived fixture
/// (<see cref="InMemoryJobStoreParityTests"/>, <see cref="SqliteJobStoreParityTests"/>,
/// <see cref="PostgresJobStoreParityTests"/>). All three passing the same assertions is the strongest
/// guarantee that the EF store is a behavioral drop-in for the in-memory store — the contract the whole
/// "swap storage, code unchanged" promise rests on.
/// </summary>
/// <remarks>
/// <para><b>Two parity-honesty rules baked in here (else a passing assertion would lie):</b></para>
/// <list type="number">
/// <item><b><c>object?</c> → <see cref="JsonElement"/>.</b> The InMemory store keeps the raw boxed CLR
/// value; the EF store round-trips the <c>Parameters</c> dictionary through JSON, so values come back as
/// <see cref="JsonElement"/> (documented contract — <c>JsonColumn.cs</c> remarks). A naive
/// <c>Parameters["k"].Should.Be("EU")</c> would pass on InMemory and FALSELY FAIL on EF. We compare the
/// NORMALIZED string form (<see cref="ParamString"/>), which is the documented equality axis
/// ("equality is by serialized form, not CLR-type identity").</item>
/// <item><b>Collation.</b> DB string ordering follows the column collation, not <c>StringComparer.Ordinal</c>.
/// PostgreSQL's default <c>en_US.utf8</c> and SQLite's <c>BINARY</c> agree with Ordinal ONLY for a safe
/// character set; the runtime uses UUIDv7 "N" hex ids (<c>[0-9a-f]</c>). So the tiebreak test
/// uses lowercase-ASCII ids, staying inside the collation-agreement guarantee — testing what the product
/// actually relies on, not an unsupported mixed-case ordering.</item>
/// </list>
/// </remarks>
public abstract class JobStoreParityTestBase : IAsyncLifetime
{
    private protected static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The deterministic clock both the InMemory and EF stores consume (shared so stamping matches).</summary>
    protected FakeTimeProvider Clock { get; private set; } = default!;

    /// <summary>The store under parity test, supplied by the derived provider fixture.</summary>
    protected IJobStoreInternal Store { get; private set; } = default!;

    /// <summary>Builds a fresh, empty store over this provider with the supplied (shared) clock.</summary>
    protected abstract Task<IJobStoreInternal> CreateStoreAsync(FakeTimeProvider clock);

    /// <summary>Tears down provider resources (DB connection / container database). Default no-op (InMemory).</summary>
    protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        Clock = new FakeTimeProvider(T0);
        Store = await CreateStoreAsync(Clock);
    }

    public async Task DisposeAsync() => await DisposeStoreAsync();

    private async Task SeedAsync(params JobExecution[] executions)
    {
        foreach (var e in executions)
        {
            await Store.InsertAsync(e, CancellationToken.None);
        }
    }

    /// <summary>
    /// Normalizes a <c>Parameters</c> value to its string form regardless of provider representation
    /// (CLR <see cref="string"/> on InMemory, <see cref="JsonElement"/> on EF) — see class remarks rule 1.
    /// </summary>
    private static string? ParamString(JobExecution e, string key)
    {
        if (!e.Parameters.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }
        return v is JsonElement je ? je.ToString() : v.ToString();
    }

    // ===== round-trip / JSON column =====

    [Fact]
    public async Task InsertThenGet_RoundTripsCoreFields_IncludingJsonParameters()
    {
        await SeedAsync(TestData.Execution(
            "e1",
            jobName: "Invoice.Process",
            batchId: "run-1",
            batchStepId: "step-1",
            batchDefinitionId: "def-1",
            status: JobStatus.Pending,
            enqueuedAtUtc: T0,
            parameters: new Dictionary<string, object?> { ["region"] = "EU", ["attempt"] = "3" },
            maxRetries: 2,
            workerName: "w-1",
            triggeredBy: "user@x"));

        var fetched = (await Store.GetAsync("e1", CancellationToken.None))!;

        fetched.ExecutionId.Should().Be("e1");
        fetched.JobName.Should().Be("Invoice.Process");
        fetched.BatchId.Should().Be("run-1");
        fetched.BatchStepId.Should().Be("step-1");
        fetched.BatchDefinitionId.Should().Be("def-1", "the batch-definition attribution field must survive the round-trip");
        fetched.Status.Should().Be(JobStatus.Pending);
        fetched.EnqueuedAtUtc.Should().Be(T0);
        fetched.MaxRetries.Should().Be(2);
        fetched.WorkerName.Should().Be("w-1");
        fetched.TriggeredBy.Should().Be("user@x");

        // JSON column parity: compared by NORMALIZED string form (object? → JsonElement on EF).
        fetched.Parameters.Should().HaveCount(2);
        ParamString(fetched, "region").Should().Be("EU");
        ParamString(fetched, "attempt").Should().Be("3");
    }

    [Fact]
    public async Task InsertThenGet_ExplicitNullParameterValue_KeySurvivesWithNullValue()
    {
        // An explicit-null parameter value (e.g. {"customerId": null}) is meaningful data and MUST survive
        // the round-trip on every provider: the EF JSON column serializes it as "k":null and reads it back
        // as a present key with a null value — matching the in-memory store, which keeps the key too. The
        // normalized value is null on both (ParamString returns null for a present-but-null entry), so the
        // distinguishing assertion is key PRESENCE, not the value alone.
        await SeedAsync(TestData.Execution(
            "with-null",
            parameters: new Dictionary<string, object?> { ["customerId"] = null, ["region"] = "EU" }));

        var fetched = (await Store.GetAsync("with-null", CancellationToken.None))!;

        fetched.Parameters.Should().HaveCount(2, "the explicit-null key must not be dropped on persist");
        fetched.Parameters.ContainsKey("customerId").Should().BeTrue("an explicit null value keeps its key");
        ParamString(fetched, "customerId").Should().BeNull("the persisted value round-trips as null");
        ParamString(fetched, "region").Should().Be("EU", "the sibling non-null key is intact");
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        (await Store.GetAsync("nope", CancellationToken.None)).Should().BeNull();
    }

    // ===== query: filters =====

    [Fact]
    public async Task Query_FilterByStatuses_ReturnsOnlyMatching()
    {
        await SeedAsync(
            TestData.Execution("e1", status: JobStatus.Completed),
            TestData.Execution("e2", status: JobStatus.Failed),
            TestData.Execution("e3", status: JobStatus.Pending));

        var result = await Store.QueryAsync(
            new JobQuery { Statuses = new[] { JobStatus.Completed, JobStatus.Failed }, Limit = 100 },
            CancellationToken.None);

        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "e1", "e2" });
    }

    [Fact]
    public async Task Query_FilterByBatchDefinitionId_StandaloneJobNeverMatches()
    {
        await SeedAsync(
            TestData.Execution("standalone", batchDefinitionId: null),
            TestData.Execution("batched", batchDefinitionId: "def-A"));

        var result = await Store.QueryAsync(
            new JobQuery { BatchDefinitionId = "def-A", Limit = 100 }, CancellationToken.None);

        result.Should().ContainSingle().Which.ExecutionId.Should().Be("batched");
    }

    [Fact]
    public async Task Query_SearchText_CaseInsensitive_AcrossJobNameAndLastError()
    {
        // the PG ILIKE / SQLite LIKE / InMemory OrdinalIgnoreCase branches must agree.
        // A MIXED-CASE needle is the discriminating case (a case-sensitive match would drop e1+e2).
        await SeedAsync(
            TestData.Execution("e1", jobName: "InvoiceProcessing"),
            TestData.Execution("e2", jobName: "other", lastError: "Invoice failed validation"),
            TestData.Execution("e3", jobName: "unrelated"));

        var result = await Store.QueryAsync(
            new JobQuery { SearchText = "iNvOiCe", Limit = 100 }, CancellationToken.None);

        result.Select(e => e.ExecutionId).Should().BeEquivalentTo(
            new[] { "e1", "e2" }, "case-insensitive substring across JobName + LastError on every provider");
    }

    [Fact]
    public async Task Query_SearchText_LiteralPercent_MatchesLiterally_NotAsWildcard()
    {
        // a literal % in the needle must NOT behave as a LIKE wildcard (escaping parity).
        await SeedAsync(
            TestData.Execution("pct", jobName: "100%done"),
            TestData.Execution("nopct", jobName: "100done"));

        var result = await Store.QueryAsync(
            new JobQuery { SearchText = "100%done", Limit = 100 }, CancellationToken.None);

        result.Should().ContainSingle().Which.ExecutionId.Should().Be("pct");
    }

    [Fact]
    public async Task Query_FromInclusive_ToExclusive_TimeBounds()
    {
        await SeedAsync(
            TestData.Execution("early", enqueuedAtUtc: T0),
            TestData.Execution("onbound", enqueuedAtUtc: T0.AddHours(1)),
            TestData.Execution("late", enqueuedAtUtc: T0.AddHours(2)));

        var fromResult = await Store.QueryAsync(new JobQuery { FromUtc = T0.AddHours(1), Limit = 100 }, CancellationToken.None);
        fromResult.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "onbound", "late" }, "FromUtc is inclusive (>=)");

        var toResult = await Store.QueryAsync(new JobQuery { ToUtc = T0.AddHours(1), Limit = 100 }, CancellationToken.None);
        toResult.Select(e => e.ExecutionId).Should().BeEquivalentTo(new[] { "early" }, "ToUtc is exclusive (<)");
    }

    // ===== query: ordering / paging =====

    [Fact]
    public async Task Query_DescendingByEnqueuedAt_NewestFirst()
    {
        await SeedAsync(
            TestData.Execution("old", enqueuedAtUtc: T0),
            TestData.Execution("mid", enqueuedAtUtc: T0.AddHours(1)),
            TestData.Execution("new", enqueuedAtUtc: T0.AddHours(2)));

        var result = await Store.QueryAsync(new JobQuery { DescendingByEnqueuedAt = true, Limit = 100 }, CancellationToken.None);
        result.Select(e => e.ExecutionId).Should().Equal("new", "mid", "old");
    }

    [Fact]
    public async Task Query_ExecutionIdTiebreak_OnEqualTimestamps()
    {
        // Equal timestamps → tie broken by ExecutionId ascending on every provider. Lowercase-ASCII ids
        // stay inside the BINARY/en_US/Ordinal collation-agreement set (class remarks rule 2).
        await SeedAsync(
            TestData.Execution("c", enqueuedAtUtc: T0),
            TestData.Execution("a", enqueuedAtUtc: T0),
            TestData.Execution("b", enqueuedAtUtc: T0));

        var asc = await Store.QueryAsync(new JobQuery { DescendingByEnqueuedAt = false, Limit = 100 }, CancellationToken.None);
        asc.Select(e => e.ExecutionId).Should().Equal("a", "b", "c");

        var desc = await Store.QueryAsync(new JobQuery { DescendingByEnqueuedAt = true, Limit = 100 }, CancellationToken.None);
        // Tiebreak stays ExecutionId-ascending even when time is descending (ThenBy after OrderByDescending).
        desc.Select(e => e.ExecutionId).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Query_Paging_OffsetAndLimit()
    {
        for (var i = 0; i < 10; i++)
        {
            await Store.InsertAsync(TestData.Execution($"e{i:D2}", enqueuedAtUtc: T0.AddMinutes(i)), CancellationToken.None);
        }

        var page = await Store.QueryAsync(
            new JobQuery { Offset = 2, Limit = 3, DescendingByEnqueuedAt = false }, CancellationToken.None);
        page.Select(e => e.ExecutionId).Should().Equal("e02", "e03", "e04");
    }

    [Fact]
    public async Task Count_RespectsFilter_IgnoresPaging()
    {
        await SeedAsync(
            TestData.Execution("e1", status: JobStatus.Completed),
            TestData.Execution("e2", status: JobStatus.Completed),
            TestData.Execution("e3", status: JobStatus.Failed));

        var count = await Store.CountAsync(
            new JobQuery { Statuses = new[] { JobStatus.Completed }, Offset = 1, Limit = 1 }, CancellationToken.None);

        count.Should().Be(2, "Count applies the filter but ignores Offset/Limit");
    }

    // ===== writer: state machine + stamping =====

    [Fact]
    public async Task UpdateStatus_LegalTransitions_StampStartedAndCompleted()
    {
        await SeedAsync(TestData.Execution("e1", status: JobStatus.Pending));

        await Store.UpdateStatusAsync("e1", JobStatus.Running, null, CancellationToken.None);
        var running = (await Store.GetAsync("e1", CancellationToken.None))!;
        running.Status.Should().Be(JobStatus.Running);
        running.StartedAtUtc.Should().Be(T0, "first Running stamps StartedAt from the clock");
        running.CompletedAtUtc.Should().BeNull();

        Clock.Advance(TimeSpan.FromMinutes(5));
        await Store.UpdateStatusAsync("e1", JobStatus.Completed, null, CancellationToken.None);
        var done = (await Store.GetAsync("e1", CancellationToken.None))!;
        done.Status.Should().Be(JobStatus.Completed);
        done.StartedAtUtc.Should().Be(T0, "StartedAt is not overwritten on later transitions");
        done.CompletedAtUtc.Should().Be(T0.AddMinutes(5), "terminal stamps CompletedAt from the clock");
    }

    [Fact]
    public async Task UpdateStatus_IllegalTransition_ThrowsInvalidOperation()
    {
        await SeedAsync(TestData.Execution("e1", status: JobStatus.Pending));

        var act = async () => await Store.UpdateStatusAsync("e1", JobStatus.Completed, null, CancellationToken.None);

        // Parity is at the CONTRACT level: both stores throw InvalidOperationException and both name the
        // rejected transition. InMemory routes through Core's InvalidJobTransitionException and the EF
        // adapter calls the Abstractions-public JobStatusTransitions.Validate; both now phrase the
        // message as "Illegal job status transition", but the wording is not part of the cross-store
        // contract — we assert the frozen type + that the offending transition is identified.
        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("Pending").And.Contain("Completed");
    }

    [Fact]
    public async Task UpdateStatus_NotFound_ThrowsKeyNotFound()
    {
        var act = async () => await Store.UpdateStatusAsync("ghost", JobStatus.Running, null, CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Insert_DuplicateId_ThrowsInvalidOperation_WithParityMessage()
    {
        await SeedAsync(TestData.Execution("dup"));

        var act = async () => await Store.InsertAsync(TestData.Execution("dup"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*", "the EF unique-violation message mirrors InMemory verbatim");
    }
}
