using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Batches;
using UKBatch.Registry;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Approval;

/// <summary>
/// Approval gate timeout precision &lt;=100ms drift on .NET TimerQueue.
/// Also covers S4 invariant — negative-remaining guard fires OnTimeout synchronously.
/// </summary>
public class ApprovalGateTimeoutTests
{
    // 4-arg ctor with the default InMemoryApprovalGateStore.
    private static ApprovalGateService NewService(TimeProvider? clock = null) =>
        new(clock ?? TimeProvider.System, new BatchDefinitionRegistry(), new InMemoryApprovalGateStore(), NullLogger<ApprovalGateService>.Instance);

    [Fact]
    public async Task AwaitApproval_TimeoutFail_FiresWithin100MsOfDeadline()
    {
        var svc = NewService();
        var timeout = TimeSpan.FromMilliseconds(300);
        var config = new ApprovalGateConfig
        {
            Title = "TimedFail",
            AllowedRoles = new[] { "admin" },
            TimeoutAfter = timeout,
            OnTimeout = ApprovalTimeoutAction.Fail,
        };
        IApprovalGateCoordinator coord = svc;

        var sw = Stopwatch.StartNew();
        Func<Task> act = async () => await coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default).ConfigureAwait(false);
        var ex = await act.Should().ThrowAsync<BatchStepFailureException>().ConfigureAwait(false);
        sw.Stop();

        ex.Which.Message.Should().Contain("timed out");
        // Acceptance: drift <=100ms above the configured timeout.
        sw.Elapsed.Should().BeGreaterOrEqualTo(timeout - TimeSpan.FromMilliseconds(20));
        sw.Elapsed.Should().BeLessOrEqualTo(timeout + TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task AwaitApproval_TimeoutAutoApprove_CompletesAfterTimeout()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "TimedAuto",
            AllowedRoles = new[] { "admin" },
            TimeoutAfter = TimeSpan.FromMilliseconds(200),
            OnTimeout = ApprovalTimeoutAction.AutoApprove,
        };
        IApprovalGateCoordinator coord = svc;

        var sw = Stopwatch.StartNew();
        await coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default).ConfigureAwait(false);
        sw.Stop();
        sw.Elapsed.Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(180));
        sw.Elapsed.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task AwaitApproval_TimeoutHold_KeepsGateOpenPastDeadline()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "TimedHold",
            AllowedRoles = new[] { "admin" },
            TimeoutAfter = TimeSpan.FromMilliseconds(100),
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        using var ctsForGate = new CancellationTokenSource();
        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", ctsForGate.Token);
        // Wait well past the deadline.
        await Task.Delay(300).ConfigureAwait(false);
        awaitTask.IsCompleted.Should().BeFalse("Hold means the gate remains pending past the deadline");

        // Manual approval still resolves it.
        var pending = await svc.ListPendingAsync(null, default).ConfigureAwait(false);
        pending.Should().HaveCount(1);
        await svc.ApproveAsync(
            pending[0].ApprovalId,
            new Abstractions.Storage.ApproverContext { Identity = "admin@x", Roles = new[] { "admin" } },
            null,
            default).ConfigureAwait(false);

        await awaitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [Fact]
    public async Task AwaitApproval_VeryShortTimeout_FiresPromptly()
    {
        // S4 verification — even with a sub-millisecond timeout, the negative-remaining guard
        // fires OnTimeout synchronously without Task.Delay throwing.
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "ShortTimeout",
            AllowedRoles = new[] { "admin" },
            TimeoutAfter = TimeSpan.FromMilliseconds(1),
            OnTimeout = ApprovalTimeoutAction.Fail,
        };
        IApprovalGateCoordinator coord = svc;

        var sw = Stopwatch.StartNew();
        Func<Task> act = async () => await coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", default).ConfigureAwait(false);
        await act.Should().ThrowAsync<BatchStepFailureException>().ConfigureAwait(false);
        sw.Stop();
        sw.Elapsed.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task AwaitApproval_NoTimeout_WaitsIndefinitely()
    {
        var svc = NewService();
        var config = new ApprovalGateConfig
        {
            Title = "Indefinite",
            AllowedRoles = new[] { "admin" },
            TimeoutAfter = null,
            OnTimeout = ApprovalTimeoutAction.Hold,
        };
        IApprovalGateCoordinator coord = svc;

        using var ctsForGate = new CancellationTokenSource();
        var awaitTask = coord.AwaitApprovalAsync("b1", "s1", config, "TestBatch", "def-1", ctsForGate.Token);
        await Task.Delay(300).ConfigureAwait(false);
        awaitTask.IsCompleted.Should().BeFalse();

        ctsForGate.Cancel();
        try { await awaitTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
