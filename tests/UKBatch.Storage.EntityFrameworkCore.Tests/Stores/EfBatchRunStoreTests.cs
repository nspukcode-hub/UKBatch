using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using UKBatch.Abstractions.Models;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Stores;

/// <summary>
/// <see cref="EfBatchRunStore"/> behavior on SQLite with direct stored-column assertions: the nullable
/// status persists as a NAME (not an integer) and round-trips null↔running, completion mutates the tracked
/// row in place, and a duplicate primary key surfaces the parity message.
/// </summary>
public sealed class EfBatchRunStoreTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfBatchRunStore _store = default!;

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync();
        _store = new EfBatchRunStore(_harness.Factory);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task CreateAsync_RunningRun_PersistsNullStatusColumn()
    {
        await _store.CreateAsync(TestData.BatchRun("r1", status: null), CancellationToken.None);

        await using var db = await _harness.NewContextAsync();
        var rawStatus = await db.Database
            .SqlQueryRaw<string?>("SELECT Status AS Value FROM BatchRuns WHERE BatchId = 'r1'")
            .SingleAsync();
        rawStatus.Should().BeNull("a running run stores a NULL status column");
    }

    [Fact]
    public async Task CompleteAsync_PersistsStatusAsName_NotInteger()
    {
        await _store.CreateAsync(TestData.BatchRun("r1"), CancellationToken.None);
        await _store.CompleteAsync("r1", JobStatus.Failed, new BatchRunCounts(2, 1, 1, 0), T0.AddMinutes(1), CancellationToken.None);

        await using var db = await _harness.NewContextAsync();
        var rawStatus = await db.Database
            .SqlQueryRaw<string>("SELECT Status AS Value FROM BatchRuns WHERE BatchId = 'r1'")
            .SingleAsync();
        rawStatus.Should().Be("Failed", "the status enum persists as its NAME, not an integer");
    }

    [Fact]
    public async Task CompleteAsync_MutatesTrackedRow_StampsCountsAndCompletedAt()
    {
        await _store.CreateAsync(TestData.BatchRun("r1", stepCount: 4), CancellationToken.None);
        await _store.CompleteAsync("r1", JobStatus.Completed, new BatchRunCounts(4, 4, 0, 0), T0.AddMinutes(5), CancellationToken.None);

        var fetched = (await _store.GetAsync("r1", CancellationToken.None))!;
        fetched.Status.Should().Be(JobStatus.Completed);
        fetched.Total.Should().Be(4);
        fetched.Succeeded.Should().Be(4);
        fetched.CompletedAtUtc.Should().Be(T0.AddMinutes(5));
        fetched.StepCount.Should().Be(4, "completion preserves the create-time step count");

        // Exactly one row — completion is an in-place update, not an insert.
        await using var db = await _harness.NewContextAsync();
        var rowCount = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM BatchRuns WHERE BatchId = 'r1'")
            .SingleAsync();
        rowCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_DuplicateId_ThrowsInvalidOperation()
    {
        await _store.CreateAsync(TestData.BatchRun("dup"), CancellationToken.None);

        var act = async () => await _store.CreateAsync(TestData.BatchRun("dup"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task CompleteAsync_AbsentRow_IsNoOp_DoesNotInsert()
    {
        var act = async () => await _store.CompleteAsync(
            "ghost", JobStatus.Failed, new BatchRunCounts(1, 0, 1, 0), T0, CancellationToken.None);
        await act.Should().NotThrowAsync();

        (await _store.GetAsync("ghost", CancellationToken.None)).Should().BeNull();
    }
}
