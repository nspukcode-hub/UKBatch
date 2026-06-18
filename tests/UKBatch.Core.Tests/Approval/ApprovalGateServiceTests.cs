using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;
using UKBatch.Registry;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Approval;

/// <summary>
/// Approval gate behaviour: Approve / Reject / authorisation / fail-safe empty AllowedRoles.
/// </summary>
public class ApprovalGateServiceTests
{
    // ApprovalGateService gained a 4th ctor dep (IApprovalGateStore).
    // The default InMemoryApprovalGateStore keeps these tests behaviorally unchanged.
    private static ApprovalGateService NewService(TimeProvider? clock = null) =>
        new(clock ?? TimeProvider.System, new BatchDefinitionRegistry(), new InMemoryApprovalGateStore(), NullLogger<ApprovalGateService>.Instance);

    [Fact]
    public async Task AwaitApprovalAsync_Approved_CompletesSuccessfully()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { "admin" },
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default);
        // Give the gate a moment to register.
        await Task.Delay(50).ConfigureAwait(false);

        var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        pending.Should().HaveCount(1);
        var approvalId = pending[0].ApprovalId;

        await svc.ApproveAsync(approvalId, new ApproverContext { Identity = "admin@x", Roles = new[] { "admin" } }, "ok", default).ConfigureAwait(false);

        await awaitTask.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    [Fact]
    public async Task AwaitApprovalAsync_Rejected_ThrowsBatchStepFailure()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { "admin" },
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default);
        await Task.Delay(50).ConfigureAwait(false);

        var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        var approvalId = pending[0].ApprovalId;

        await svc.RejectAsync(approvalId, new ApproverContext { Identity = "admin@x", Roles = new[] { "admin" } }, "no thanks", default).ConfigureAwait(false);

        Func<Task> act = async () => await awaitTask.ConfigureAwait(false);
        await act.Should().ThrowAsync<BatchStepFailureException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task ApproveAsync_EmptyAllowedRoles_ThrowsInvalidOperation()
    {
        // Fail-safe — empty AllowedRoles means NOBODY can approve.
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = Array.Empty<string>(),
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        using var ctsForGate = new CancellationTokenSource();
        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", ctsForGate.Token);
        await Task.Delay(50).ConfigureAwait(false);

        var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        var approvalId = pending[0].ApprovalId;

        Func<Task> act = async () =>
            await svc.ApproveAsync(approvalId, new ApproverContext { Identity = "a", Roles = new[] { "admin" } }, null, default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fail-safe deadlock*").ConfigureAwait(false);

        // Clean up gate.
        ctsForGate.Cancel();
        try { await awaitTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ApproveAsync_RoleMismatch_ThrowsInvalidOperation()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { "admin" },
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        using var ctsForGate = new CancellationTokenSource();
        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", ctsForGate.Token);
        await Task.Delay(50).ConfigureAwait(false);

        var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        var approvalId = pending[0].ApprovalId;

        Func<Task> act = async () =>
            await svc.ApproveAsync(approvalId, new ApproverContext { Identity = "u", Roles = new[] { "viewer" } }, null, default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lacks any of the allowed roles*").ConfigureAwait(false);

        ctsForGate.Cancel();
        try { await awaitTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ApproveAsync_AnyAuthenticatedUserSentinel_AcceptsAnyApprover()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { ApprovalGateConfig.AnyAuthenticatedUser },
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default);
        await Task.Delay(50).ConfigureAwait(false);

        var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        var approvalId = pending[0].ApprovalId;

        await svc.ApproveAsync(approvalId, new ApproverContext { Identity = "viewer@x", Roles = new[] { "viewer" } }, null, default).ConfigureAwait(false);
        await awaitTask.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    [Fact]
    public async Task ApproveAsync_UnknownApprovalId_ThrowsInvalidOperation()
    {
        var svc = NewService();
        Func<Task> act = async () =>
            await svc.ApproveAsync("nonexistent", new ApproverContext { Identity = "x", Roles = Array.Empty<string>() }, null, default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*").ConfigureAwait(false);
    }

    [Fact]
    public async Task ListPendingAsync_FiltersByRole()
    {
        var svc = NewService();
        var configA = new ApprovalGateConfig { Title = "A", AllowedRoles = new[] { "admin" }, OnTimeout = ApprovalTimeoutAction.Hold };
        var configB = new ApprovalGateConfig { Title = "B", AllowedRoles = new[] { "ops" }, OnTimeout = ApprovalTimeoutAction.Hold };
        IApprovalGateCoordinator coord = svc;

        using var ctsForGate = new CancellationTokenSource();
        var t1 = coord.AwaitApprovalAsync("b1", "s1", configA, "TestBatch", "def-1", ctsForGate.Token);
        var t2 = coord.AwaitApprovalAsync("b1", "s2", configB, "TestBatch", "def-1", ctsForGate.Token);
        await Task.Delay(50).ConfigureAwait(false);

        var asAdmin = await svc.ListPendingAsync("admin", default).ConfigureAwait(false);
        asAdmin.Should().HaveCount(1).And.Subject.First().Config.Title.Should().Be("A");

        var asOps = await svc.ListPendingAsync("ops", default).ConfigureAwait(false);
        asOps.Should().HaveCount(1).And.Subject.First().Config.Title.Should().Be("B");

        var asAll = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        asAll.Should().HaveCount(2);

        ctsForGate.Cancel();
        try { await Task.WhenAll(t1, t2).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task AwaitApprovalAsync_CancellationToken_PropagatesAsOperationCanceled()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { "admin" },
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        using var cts = new CancellationTokenSource();
        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", cts.Token);
        await Task.Delay(50).ConfigureAwait(false);
        cts.Cancel();

        Func<Task> act = async () => await awaitTask.ConfigureAwait(false);
        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task AwaitApprovalAsync_CreatePersistFails_DoesNotLeakRegistration()
    {
        // Regression: if the create-time SaveAsync fails (store unreachable) or is cancelled AFTER the
        // in-memory registration is inserted but BEFORE the shared gate lifecycle (whose finally removes
        // it), the registration MUST be rolled back. Otherwise it survives as an un-decidable,
        // un-removable ghost pending gate — and grows the dict unbounded under a flaky store.
        var svc = new ApprovalGateService(
            TimeProvider.System,
            new BatchDefinitionRegistry(),
            new ThrowingSaveApprovalGateStore(),
            NullLogger<ApprovalGateService>.Instance);
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { "admin" },
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        // The persist failure propagates to the caller (the batch step fails honestly).
        Func<Task> act = async () =>
            await coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*simulated store failure*").ConfigureAwait(false);

        // No ghost gate is left behind: the live dict was rolled back and the throwing store persisted
        // nothing, so the merged pending list is empty.
        var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentApproveAndReject_RecordsConsistentDeciderForWinningOutcome()
    {
        // Concurrency regression: a concurrent approve + reject on the SAME gate must never cross outcome
        // and attribution. The outcome (Approved/Rejected) has always been correct (single-winner tcs);
        // the bug was that the LOSER could overwrite the WINNER's decider/note, so the audit record could
        // read e.g. "Approved by rejecter, note=<reject reason>". Run many races; every recorded decision
        // must be internally consistent.
        for (var iter = 0; iter < 50; iter++)
        {
            var store = new CapturingApprovalGateStore();
            var svc = new ApprovalGateService(
                TimeProvider.System, new BatchDefinitionRegistry(), store, NullLogger<ApprovalGateService>.Instance);
            var config = new ApprovalGateConfig
            {
                Title = "Confirm",
                AllowedRoles = new[] { "ops" },
                OnTimeout = ApprovalTimeoutAction.Hold,
            };
            IApprovalGateCoordinator coord = svc;

            var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default);

            // Wait until the gate is registered (pending) before racing the two decisions.
            string? approvalId = null;
            for (var w = 0; w < 200 && approvalId is null; w++)
            {
                var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
                if (pending.Count == 1) { approvalId = pending[0].ApprovalId; }
                else { await Task.Delay(5).ConfigureAwait(false); }
            }
            approvalId.Should().NotBeNull("the gate should become pending");

            var approver = new ApproverContext { Identity = "approver", Roles = new[] { "ops" } };
            var rejecter = new ApproverContext { Identity = "rejecter", Roles = new[] { "ops" } };

            // The loser of the race can legitimately see the gate ALREADY resolved + removed (a 404
            // ApprovalNotFoundException), not only the claim-bail path — both mean "your decision didn't
            // take". Either is fine; the real assertion is the recorded outcome's consistency below.
            var t1 = Task.Run(async () =>
            {
                try { await svc.ApproveAsync(approvalId!, approver, "approved-note", default).ConfigureAwait(false); }
                catch (ApprovalNotFoundException) { }
            });
            var t2 = Task.Run(async () =>
            {
                try { await svc.RejectAsync(approvalId!, rejecter, "rejected-reason", default).ConfigureAwait(false); }
                catch (ApprovalNotFoundException) { }
            });
            await Task.WhenAll(t1, t2).ConfigureAwait(false);

            // Drain the gate's resolution (approve completes, reject throws — either is fine here).
            try { await awaitTask.ConfigureAwait(false); } catch (BatchStepFailureException) { }

            var recorded = store.LastOutcome;
            recorded.Should().NotBeNull("exactly one terminal record must be written");
            if (recorded!.Value.Outcome == ApprovalRecordOutcome.Approved)
            {
                recorded.Value.DecidedBy.Should().Be("approver");
                recorded.Value.Note.Should().Be("approved-note");
            }
            else
            {
                recorded.Value.Outcome.Should().Be(ApprovalRecordOutcome.Rejected);
                recorded.Value.DecidedBy.Should().Be("rejecter");
                recorded.Value.Note.Should().Be("rejected-reason");
            }
        }
    }

    /// <summary>
    /// An <see cref="IApprovalGateStore"/> whose create-time <c>SaveAsync</c> always throws — models a
    /// store/DB that is unreachable at gate-creation time. Everything else is a benign empty/no-op so the
    /// service's other paths behave normally.
    /// </summary>
    private sealed class ThrowingSaveApprovalGateStore : IApprovalGateStore
    {
        public Task SaveAsync(PersistedApprovalGate gate, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated store failure (store unreachable).");

        public Task<PersistedApprovalGate?> GetAsync(string approvalId, CancellationToken cancellationToken) =>
            Task.FromResult<PersistedApprovalGate?>(null);

        public Task<IReadOnlyList<PersistedApprovalGate>> ListPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistedApprovalGate>>(Array.Empty<PersistedApprovalGate>());

        public Task<IReadOnlyList<PersistedApprovalGate>> ListByBatchAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistedApprovalGate>>(Array.Empty<PersistedApprovalGate>());

        public Task RecordOutcomeAsync(string approvalId, ApprovalRecordOutcome outcome, string decidedBy, DateTimeOffset decidedAtUtc, string? note, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Wraps a real <see cref="InMemoryApprovalGateStore"/> and captures the single terminal
    /// <c>RecordOutcomeAsync</c> call so a test can assert the recorded (outcome, decider, note) is
    /// internally consistent.
    /// </summary>
    private sealed class CapturingApprovalGateStore : IApprovalGateStore
    {
        private readonly InMemoryApprovalGateStore _inner = new();

        public (ApprovalRecordOutcome Outcome, string DecidedBy, string? Note)? LastOutcome { get; private set; }

        public Task SaveAsync(PersistedApprovalGate gate, CancellationToken cancellationToken) =>
            _inner.SaveAsync(gate, cancellationToken);

        public Task<PersistedApprovalGate?> GetAsync(string approvalId, CancellationToken cancellationToken) =>
            _inner.GetAsync(approvalId, cancellationToken);

        public Task<IReadOnlyList<PersistedApprovalGate>> ListPendingAsync(CancellationToken cancellationToken) =>
            _inner.ListPendingAsync(cancellationToken);

        public Task<IReadOnlyList<PersistedApprovalGate>> ListByBatchAsync(string batchId, CancellationToken cancellationToken) =>
            _inner.ListByBatchAsync(batchId, cancellationToken);

        public async Task RecordOutcomeAsync(string approvalId, ApprovalRecordOutcome outcome, string decidedBy, DateTimeOffset decidedAtUtc, string? note, CancellationToken cancellationToken)
        {
            await _inner.RecordOutcomeAsync(approvalId, outcome, decidedBy, decidedAtUtc, note, cancellationToken).ConfigureAwait(false);
            LastOutcome = (outcome, decidedBy, note);
        }
    }
}
