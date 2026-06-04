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

        await awaitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
        await awaitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
}
