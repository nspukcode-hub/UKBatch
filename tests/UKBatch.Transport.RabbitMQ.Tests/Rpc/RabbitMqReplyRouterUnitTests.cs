using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Models;
using UKBatch.Transport.RabbitMQ;
using UKBatch.Transport.RabbitMQ.Connection;
using UKBatch.Transport.RabbitMQ.Rpc;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Rpc;

/// <summary>
/// <see cref="RabbitMqReplyRouter"/> pending-registry coverage (broker-free). Duplicate
/// correlation id rejection, pending slot lifecycle, and dispose-cancels-stragglers. The live
/// correlation-id → TCS completion path is exercised by the integration RPC tests (needs a broker).
/// </summary>
public sealed class RabbitMqReplyRouterUnitTests
{
    private static RabbitMqReplyRouter BuildRouter()
    {
        var manager = new RabbitMqConnectionManager(
            Microsoft.Extensions.Options.Options.Create(new RabbitMqTransportOptions()),
            Microsoft.Extensions.Options.Options.Create(new UKBatchOptions()),
            NullLogger<RabbitMqConnectionManager>.Instance);
        return new RabbitMqReplyRouter(manager, NullLogger<RabbitMqReplyRouter>.Instance);
    }

    [Fact]
    public void RegisterPending_NewCorrelationId_ReturnsIncompleteTcs()
    {
        var router = BuildRouter();
        var tcs = router.RegisterPending("corr-1");
        tcs.Task.Should().NotBeNull();
        tcs.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void RegisterPending_DuplicateCorrelationId_Throws()
    {
        var router = BuildRouter();
        router.RegisterPending("corr-1");
        var act = () => router.RegisterPending("corr-1");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate correlationId 'corr-1'*already in flight*");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void RegisterPending_BlankCorrelationId_Throws(string? correlationId)
    {
        var router = BuildRouter();
        var act = () => router.RegisterPending(correlationId!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RemovePending_AllowsReRegistrationOfSameId()
    {
        var router = BuildRouter();
        router.RegisterPending("corr-1");
        router.RemovePending("corr-1");
        // After removal the slot is free to re-register (e.g. a retry with the same MessageId).
        var act = () => router.RegisterPending("corr-1");
        act.Should().NotThrow();
    }

    [Fact]
    public void RemovePending_UnknownId_IsNoOp()
    {
        var router = BuildRouter();
        var act = () => router.RemovePending("never-registered");
        act.Should().NotThrow();
    }

    [Fact]
    public void RemovePending_BlankId_IsNoOp()
    {
        var router = BuildRouter();
        var act = () => router.RemovePending("");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_CancelsStragglerPendingRequests()
    {
        var router = BuildRouter();
        var tcs = router.RegisterPending("corr-1");

        await router.DisposeAsync();

        tcs.Task.IsCanceled.Should().BeTrue(
            "dispose fails stragglers so awaiting RPCs unblock instead of hanging to timeout");
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var router = BuildRouter();
        await router.DisposeAsync();
        var act = async () => await router.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureStartedAsync_AfterDispose_Throws()
    {
        var router = BuildRouter();
        await router.DisposeAsync();
        var act = async () => await router.EnsureStartedAsync(CancellationToken.None);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void RegisterPending_DistinctIds_AllSucceed()
    {
        var router = BuildRouter();
        var t1 = router.RegisterPending("a");
        var t2 = router.RegisterPending("b");
        var t3 = router.RegisterPending("c");
        ReferenceEquals(t1, t2).Should().BeFalse();
        ReferenceEquals(t2, t3).Should().BeFalse();
        t1.Task.IsCompleted.Should().BeFalse();
    }
}
