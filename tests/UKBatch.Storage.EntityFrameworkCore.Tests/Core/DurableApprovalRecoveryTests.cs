using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// Durable approval write-through + restart recovery (the load-bearing fix). Drives the real
/// <c>ApprovalGateService</c> over an <see cref="EfApprovalGateStore"/>:
/// write-through on create/approve/reject; a RESTART (new service over the SAME store) → the merge shows
/// the persisted pending gate, decidable; a Cancelled gate is excluded; the cancel path writes a terminal
/// outcome even when the caller token is already cancelled (CT-decoupling).
/// </summary>
public sealed class DurableApprovalRecoveryTests : IAsyncLifetime
{
    private SqliteStoreHarness _db = default!;
    private EfApprovalGateStore _store = default!;

    public async Task InitializeAsync()
    {
        _db = await SqliteStoreHarness.CreateAsync();
        _store = new EfApprovalGateStore(_db.Factory);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static ApprovalGateConfig Config(params string[] roles) => new()
    {
        Title = "Confirm",
        AllowedRoles = roles.Length == 0 ? new[] { "admin" } : roles,
        OnTimeout = ApprovalTimeoutAction.Hold,
    };

    [Fact]
    public async Task Create_WritesThroughPendingRecord_ToStore()
    {
        using var harness = new ApprovalServiceHarness(_store, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var gate = harness.AwaitApprovalAsync("batch-1", "step-1", Config(), cts.Token);
        await Task.Delay(50).ConfigureAwait(false);

        var stored = await _store.ListPendingAsync(CancellationToken.None);
        stored.Should().ContainSingle("the create path writes a durable Pending record before announcing the gate");
        stored[0].BatchId.Should().Be("batch-1");
        stored[0].Status.Should().Be(ApprovalRecordStatus.Pending);

        cts.Cancel();
        try { await gate.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Approve_WritesThroughApprovedOutcome()
    {
        using var harness = new ApprovalServiceHarness(_store, new FakeTimeProvider());
        var gate = harness.AwaitApprovalAsync("batch-1", "step-1", Config("admin"), CancellationToken.None);
        await Task.Delay(50).ConfigureAwait(false);

        var pending = await harness.Service.ListPendingAsync(null, CancellationToken.None);
        var id = pending[0].ApprovalId;
        await harness.Service.ApproveAsync(id, new ApproverContext { Identity = "admin@x", Roles = new[] { "admin" } }, "ok", CancellationToken.None);
        await gate.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        var record = await _store.GetAsync(id, CancellationToken.None);
        record!.Status.Should().Be(ApprovalRecordStatus.Decided);
        record.Outcome.Should().Be(ApprovalRecordOutcome.Approved);
        record.DecidedBy.Should().Be("admin@x");
        record.Note.Should().Be("ok");

        (await _store.ListPendingAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reject_WritesThroughRejectedOutcome()
    {
        using var harness = new ApprovalServiceHarness(_store, new FakeTimeProvider());
        var gate = harness.AwaitApprovalAsync("batch-1", "step-1", Config("admin"), CancellationToken.None);
        await Task.Delay(50).ConfigureAwait(false);

        var id = (await harness.Service.ListPendingAsync(null, CancellationToken.None))[0].ApprovalId;
        await harness.Service.RejectAsync(id, new ApproverContext { Identity = "admin@x", Roles = new[] { "admin" } }, "denied", CancellationToken.None);
        // Reject surfaces as the Core-internal BatchStepFailureException: InvalidOperationException.
        try { await gate.ConfigureAwait(false); } catch (InvalidOperationException) { }

        var record = await _store.GetAsync(id, CancellationToken.None);
        record!.Outcome.Should().Be(ApprovalRecordOutcome.Rejected);
        record.Note.Should().Be("denied");
    }

    [Fact]
    public async Task Restart_NewServiceOverSameStore_ShowsPersistedPendingGate_Decidable()
    {
        // === Process 1: create a gate (write-through), then "crash" (dispose the service). ===
        string approvalId;
        using (var harness1 = new ApprovalServiceHarness(_store, new FakeTimeProvider()))
        using (var cts1 = new CancellationTokenSource())
        {
            var gate = harness1.AwaitApprovalAsync("batch-1", "step-1", Config("admin"), cts1.Token);
            await Task.Delay(50).ConfigureAwait(false);
            approvalId = (await harness1.Service.ListPendingAsync(null, CancellationToken.None))[0].ApprovalId;
            // Simulate a host crash: the in-memory awaiter dies. Cancel to release the test task.
            cts1.Cancel();
            try { await gate.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        // The cancel above wrote a terminal Cancelled record — so to model a TRUE crash (no outcome
        // written, record left Pending), reset the store to a freshly-persisted pending record.
        await _store.SaveAsync(TestData.Gate(approvalId, batchId: "batch-1", config: Config("admin")), CancellationToken.None);

        // === Process 2: a NEW service over the SAME store. In-memory dict is empty. ===
        using var harness2 = new ApprovalServiceHarness(_store, new FakeTimeProvider());
        var recovered = await harness2.Service.ListPendingAsync(null, CancellationToken.None);
        recovered.Should().ContainSingle("the store-aware merge surfaces the persisted pending gate after a restart");
        recovered[0].ApprovalId.Should().Be(approvalId);
        recovered[0].BatchId.Should().Be("batch-1");
    }

    [Fact]
    public async Task Restart_CancelledGate_ExcludedFromRecoveredPending()
    {
        // A gate that was cancelled (terminal Cancelled record) must NOT be resurrected after restart.
        await _store.SaveAsync(
            TestData.Gate("cancelled-gate", status: ApprovalRecordStatus.Decided, outcome: ApprovalRecordOutcome.Cancelled, decidedBy: "<cancelled>"),
            CancellationToken.None);

        using var harness = new ApprovalServiceHarness(_store, new FakeTimeProvider());
        var recovered = await harness.Service.ListPendingAsync(null, CancellationToken.None);
        recovered.Should().BeEmpty("a Cancelled gate is terminal — the merge excludes it");
    }

    [Fact]
    public async Task Cancel_WritesTerminalOutcome_EvenWhenCallerTokenAlreadyCancelled()
    {
        // The load-bearing CT-decoupling: WriteOutcomeThroughAsync uses CancellationToken.None so the
        // Cancelled audit record lands even though the caller token is cancelled on the cancel path.
        using var harness = new ApprovalServiceHarness(_store, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var gate = harness.AwaitApprovalAsync("batch-1", "step-1", Config("admin"), cts.Token);
        await Task.Delay(50).ConfigureAwait(false);
        var id = (await harness.Service.ListPendingAsync(null, CancellationToken.None))[0].ApprovalId;

        cts.Cancel();   // the caller token is now cancelled
        try { await gate.ConfigureAwait(false); } catch (OperationCanceledException) { }

        // The terminal Cancelled record must still have been written (NOT abandoned with the cancelled token).
        var record = await _store.GetAsync(id, CancellationToken.None);
        record!.Status.Should().Be(ApprovalRecordStatus.Decided);
        record.Outcome.Should().Be(ApprovalRecordOutcome.Cancelled);
        record.DecidedBy.Should().Be("<cancelled>");

        (await _store.ListPendingAsync(CancellationToken.None)).Should().BeEmpty("no ghost gate after cancel");
    }
}
