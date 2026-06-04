using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore;
using UKBatch.Storage.EntityFrameworkCore.Recovery;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// <see cref="OrphanedExecutionReaper"/> startup reconciliation:
/// Sweep 1 (non-terminal executions past grace → Failed with documented LastError; within-grace NOT
/// reaped; <c>OrphanGracePeriod=Zero</c> disables; the Retrying→Failed state-machine bypass) and
/// Sweep 2 (orphaned Pending gate → Interrupted; the merge then EXCLUDES it — the ghost-gate
/// regression lock). Idempotent on re-run.
/// </summary>
public sealed class OrphanedExecutionReaperTests : IAsyncLifetime
{
    private SqliteStoreHarness _harness = default!;
    private EfJobStore _jobStore = default!;
    private EfApprovalGateStore _gateStore = default!;

    // Now is the reference clock; rows enqueued at -10min are well past a 2min grace window.
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = Now.AddMinutes(-10);

    public async Task InitializeAsync()
    {
        _harness = await SqliteStoreHarness.CreateAsync(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));
        _jobStore = new EfJobStore(_harness.Factory, new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance),
            _harness.Clock, NullLogger<EfJobStore>.Instance);
        _gateStore = new EfApprovalGateStore(_harness.Factory);
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private OrphanedExecutionReaper NewReaper(TimeSpan? grace = null)
    {
        var options = new EfStorageOptions();
        options.UseSqlite("DataSource=:memory:");   // satisfies validator; harness owns the real connection
        if (grace is { } g)
        {
            options.OrphanGracePeriod = g;
        }
        return new OrphanedExecutionReaper(_harness.Factory, options, _harness.Clock, NullLogger<OrphanedExecutionReaper>.Instance);
    }

    [Fact]
    public async Task Sweep1_NonTerminalOrphan_PastGrace_ReapedToFailed()
    {
        await _jobStore.InsertAsync(TestData.Execution("running", status: JobStatus.Running, enqueuedAtUtc: LongAgo), CancellationToken.None);
        await _jobStore.InsertAsync(TestData.Execution("pending", status: JobStatus.Pending, enqueuedAtUtc: LongAgo), CancellationToken.None);

        await NewReaper().StartAsync(CancellationToken.None);

        var running = await _jobStore.GetAsync("running", CancellationToken.None);
        running!.Status.Should().Be(JobStatus.Failed);
        running.LastError.Should().Contain("Interrupted by host restart");
        running.CompletedAtUtc.Should().Be(Now);

        (await _jobStore.GetAsync("pending", CancellationToken.None))!.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task Sweep1_RetryingOrphan_ReapedToFailed_StateMachineBypass()
    {
        // Retrying→Failed has NO legal edge; the reaper writes the entity field directly (the ONE
        // sanctioned bypass). If the reaper had routed through JobStatusTransitions.Validate, this would
        // throw and the row would stay Retrying — exactly the bug the bypass prevents.
        await _jobStore.InsertAsync(TestData.Execution("retrying", status: JobStatus.Retrying, enqueuedAtUtc: LongAgo), CancellationToken.None);

        await NewReaper().StartAsync(CancellationToken.None);

        (await _jobStore.GetAsync("retrying", CancellationToken.None))!.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task Sweep1_WithinGrace_NotReaped()
    {
        // Enqueued 30s ago, grace is 2min — must NOT be reaped (protects a healthy concurrent node).
        await _jobStore.InsertAsync(TestData.Execution("fresh", status: JobStatus.Running, enqueuedAtUtc: Now.AddSeconds(-30)), CancellationToken.None);

        await NewReaper(TimeSpan.FromMinutes(2)).StartAsync(CancellationToken.None);

        (await _jobStore.GetAsync("fresh", CancellationToken.None))!.Status.Should().Be(JobStatus.Running, "within grace → untouched");
    }

    [Fact]
    public async Task Sweep1_TerminalRows_NeverTouched()
    {
        await _jobStore.InsertAsync(TestData.Execution("done", status: JobStatus.Completed, enqueuedAtUtc: LongAgo), CancellationToken.None);
        await _jobStore.InsertAsync(TestData.Execution("failed", status: JobStatus.Failed, enqueuedAtUtc: LongAgo, lastError: "original error"), CancellationToken.None);

        await NewReaper().StartAsync(CancellationToken.None);

        (await _jobStore.GetAsync("done", CancellationToken.None))!.Status.Should().Be(JobStatus.Completed);
        var failed = await _jobStore.GetAsync("failed", CancellationToken.None);
        failed!.Status.Should().Be(JobStatus.Failed);
        failed.LastError.Should().Be("original error", "an already-terminal row's LastError is not overwritten");
    }

    [Fact]
    public async Task GracePeriodZero_DisablesBothSweeps()
    {
        await _jobStore.InsertAsync(TestData.Execution("running", status: JobStatus.Running, enqueuedAtUtc: LongAgo), CancellationToken.None);
        await _gateStore.SaveAsync(TestData.Gate("g1", pendingSinceUtc: LongAgo), CancellationToken.None);

        await NewReaper(TimeSpan.Zero).StartAsync(CancellationToken.None);

        (await _jobStore.GetAsync("running", CancellationToken.None))!.Status.Should().Be(JobStatus.Running, "Zero disables the reaper");
        (await _gateStore.GetAsync("g1", CancellationToken.None))!.Status.Should().Be(ApprovalRecordStatus.Pending);
    }

    [Fact]
    public async Task Sweep2_OrphanedPendingGate_PastGrace_ReapedToInterrupted()
    {
        await _gateStore.SaveAsync(TestData.Gate("g1", pendingSinceUtc: LongAgo), CancellationToken.None);

        await NewReaper().StartAsync(CancellationToken.None);

        var gate = await _gateStore.GetAsync("g1", CancellationToken.None);
        gate!.Status.Should().Be(ApprovalRecordStatus.Decided);
        gate.Outcome.Should().Be(ApprovalRecordOutcome.Interrupted);
        gate.DecidedBy.Should().Be("<reaper>");
        gate.DecidedAtUtc.Should().Be(Now);
        gate.Note.Should().Contain("Interrupted by host restart");
    }

    [Fact]
    public async Task Sweep2_PendingGate_WithinGrace_NotReaped()
    {
        await _gateStore.SaveAsync(TestData.Gate("g1", pendingSinceUtc: Now.AddSeconds(-30)), CancellationToken.None);

        await NewReaper(TimeSpan.FromMinutes(2)).StartAsync(CancellationToken.None);

        (await _gateStore.GetAsync("g1", CancellationToken.None))!.Status.Should().Be(ApprovalRecordStatus.Pending);
    }

    [Fact]
    public async Task Sweep2_GhostGate_DoesNotResurrectInListPending()
    {
        // THE ghost-gate regression lock: an orphaned Pending gate (crash, no outcome written) is reaped
        // to Interrupted, so the store's ListPendingAsync (which filters Status==Pending) EXCLUDES it.
        await _gateStore.SaveAsync(TestData.Gate("ghost", pendingSinceUtc: LongAgo), CancellationToken.None);
        (await _gateStore.ListPendingAsync(CancellationToken.None)).Should().ContainSingle("pre-reap the ghost is still pending");

        await NewReaper().StartAsync(CancellationToken.None);

        (await _gateStore.ListPendingAsync(CancellationToken.None)).Should().BeEmpty(
            "a reaped ghost gate must NEVER reappear in the pending feed");
    }

    [Fact]
    public async Task Reaper_Idempotent_OnReRun()
    {
        await _jobStore.InsertAsync(TestData.Execution("running", status: JobStatus.Running, enqueuedAtUtc: LongAgo), CancellationToken.None);
        await _gateStore.SaveAsync(TestData.Gate("g1", pendingSinceUtc: LongAgo), CancellationToken.None);

        await NewReaper().StartAsync(CancellationToken.None);
        // Second run: everything is already terminal, so nothing changes (no throw, no double-write).
        var act = async () => await NewReaper().StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        (await _jobStore.GetAsync("running", CancellationToken.None))!.Status.Should().Be(JobStatus.Failed);
        var gate = await _gateStore.GetAsync("g1", CancellationToken.None);
        gate!.Outcome.Should().Be(ApprovalRecordOutcome.Interrupted);
        gate.DecidedAtUtc.Should().Be(Now, "the second run does not re-stamp an already-terminal gate");
    }
}
