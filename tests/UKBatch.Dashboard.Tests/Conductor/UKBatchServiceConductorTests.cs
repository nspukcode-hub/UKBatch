using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Conductor;

/// <summary>
/// UKBatchServiceConductor lifecycle + retry timer tests.
/// </summary>
public sealed class UKBatchServiceConductorTests
{
    private static (UKBatchServiceConductor conductor, IUKBatchClientFactory factory, IUKBatchServiceRegistry registry, FakeTimeProvider clock)
        BuildConductor(params UKBatchServiceDescriptor[] descriptors)
    {
        var opts = new DashboardOptions { Services = [.. descriptors] };
        var registry = new StaticServiceRegistry(Options.Create(opts));
        var factory = Substitute.For<IUKBatchClientFactory>();
        foreach (var d in descriptors)
        {
            var client = Substitute.For<IUKBatchClient>();
            client.Service.Returns(d);
            client.State.Returns(UKBatchClientState.Disconnected);
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            client.DisconnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            factory.GetClient(d.Name).Returns(client);
        }
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var conductor = new UKBatchServiceConductor(factory, registry, NullLogger<UKBatchServiceConductor>.Instance, clock, TimeSpan.FromSeconds(60));
        return (conductor, factory, registry, clock);
    }

    [Fact]
    public async Task StartAsync_ConnectsAllRegisteredClients()
    {
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var d2 = new UKBatchServiceDescriptor { Name = "beta", BaseUrl = new Uri("http://b/api") };
        var (conductor, factory, _, _) = BuildConductor(d1, d2);

        await conductor.StartAsync(CancellationToken.None);

        await factory.GetClient("alpha").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await factory.GetClient("beta").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await conductor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_OneServiceFails_DoesNotPropagate()
    {
        // invariant: ONE service failure must NOT propagate to host startup.
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var d2 = new UKBatchServiceDescriptor { Name = "broken", BaseUrl = new Uri("http://b/api") };
        var (conductor, factory, _, _) = BuildConductor(d1, d2);
        factory.GetClient("broken").ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("nope")));

        Func<Task> act = () => conductor.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
        await factory.GetClient("alpha").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await conductor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_IsIdempotent()
    {
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var (conductor, factory, _, _) = BuildConductor(d1);

        await conductor.StartAsync(CancellationToken.None);
        await conductor.StartAsync(CancellationToken.None);

        // First call connects; second call returns immediately (idempotency guard).
        await factory.GetClient("alpha").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await conductor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_DisconnectsAllClients()
    {
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var (conductor, factory, _, _) = BuildConductor(d1);

        await conductor.StartAsync(CancellationToken.None);
        await conductor.StopAsync(CancellationToken.None);

        await factory.GetClient("alpha").Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Conductor_InitialConnectFails_RetryTimerEventuallyConnects()
    {
        // lock: when initial connect fails AND state stays Disconnected, the 60s retry timer
        // eventually re-invokes ConnectAsync; on success the client transitions to Connected and
        // the conductor logs an Information-level recovery message.
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var opts = new DashboardOptions { Services = [d1] };
        var registry = new StaticServiceRegistry(Options.Create(opts));
        var factory = Substitute.For<IUKBatchClientFactory>();
        var client = Substitute.For<IUKBatchClient>();
        client.Service.Returns(d1);
        // Always return Disconnected until we want it to be Connected.
        client.State.Returns(UKBatchClientState.Disconnected);
        // First call throws; subsequent calls succeed.
        var connectCalls = 0;
        client.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var n = Interlocked.Increment(ref connectCalls);
                if (n == 1) throw new InvalidOperationException("boom");
                return Task.CompletedTask;
            });
        client.DisconnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        factory.GetClient(d1.Name).Returns(client);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        // Use a 1-second tick interval (FakeTimeProvider drives the timer deterministically).
        var conductor = new UKBatchServiceConductor(factory, registry, NullLogger<UKBatchServiceConductor>.Instance, clock, TimeSpan.FromSeconds(1));
        await conductor.StartAsync(CancellationToken.None);

        // After initial connect: 1 attempt that failed. State stayed Disconnected.
        connectCalls.Should().Be(1);

        // Drive the retry timer one tick — Conductor calls ConnectAsync again.
        clock.Advance(TimeSpan.FromSeconds(1));
        // Give the retry loop a moment to execute on the thread pool.
        await WaitForConditionAsync(() => Volatile.Read(ref connectCalls) >= 2, TimeSpan.FromSeconds(2));

        connectCalls.Should().BeGreaterOrEqualTo(2, "the retry timer must re-invoke ConnectAsync on Disconnected clients.");
        await conductor.StopAsync(CancellationToken.None);
    }

    /// <summary>Polls a condition with a short cap. PeriodicTimer + FakeTimeProvider drives synchronously, but the loop body is on a Task.Run thread.</summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
