using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Conductor;

/// <summary>
/// UKBatchServiceConductor lifecycle + retry timer tests. The initial connect is deferred until the
/// host's ApplicationStarted fires; <see cref="FakeHostLifetime.FireStarted"/> simulates that.
/// </summary>
public sealed class UKBatchServiceConductorTests
{
    private static (UKBatchServiceConductor conductor, IUKBatchClientFactory factory, IUKBatchServiceRegistry registry, FakeTimeProvider clock, FakeHostLifetime lifetime)
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
        var lifetime = new FakeHostLifetime();
        var conductor = new UKBatchServiceConductor(factory, registry, lifetime, NullLogger<UKBatchServiceConductor>.Instance, clock, TimeSpan.FromSeconds(5));
        return (conductor, factory, registry, clock, lifetime);
    }

    [Fact]
    public async Task DoesNotConnectBeforeApplicationStarted()
    {
        // The initial connect must wait for the host to finish starting — otherwise an embedded
        // dashboard connects to its own hub before it is listening and shows a false "Disconnected".
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var (conductor, factory, _, _, lifetime) = BuildConductor(d1);

        await conductor.StartAsync(CancellationToken.None);

        // ApplicationStarted has NOT fired yet → no connect attempt.
        await factory.GetClient("alpha").DidNotReceive().ConnectAsync(Arg.Any<CancellationToken>());

        lifetime.FireStarted();

        await factory.GetClient("alpha").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await conductor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectsAllRegisteredClients_OnApplicationStarted()
    {
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var d2 = new UKBatchServiceDescriptor { Name = "beta", BaseUrl = new Uri("http://b/api") };
        var (conductor, factory, _, _, lifetime) = BuildConductor(d1, d2);

        await conductor.StartAsync(CancellationToken.None);
        lifetime.FireStarted();

        await factory.GetClient("alpha").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await factory.GetClient("beta").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await conductor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OneServiceFails_DoesNotPropagate()
    {
        // invariant: ONE service failure must NOT propagate out of the startup callback.
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var d2 = new UKBatchServiceDescriptor { Name = "broken", BaseUrl = new Uri("http://b/api") };
        var (conductor, factory, _, _, lifetime) = BuildConductor(d1, d2);
        factory.GetClient("broken").ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("nope")));

        await conductor.StartAsync(CancellationToken.None);
        Action fire = () => lifetime.FireStarted();
        fire.Should().NotThrow();

        await factory.GetClient("alpha").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await conductor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_IsIdempotent()
    {
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var (conductor, factory, _, _, lifetime) = BuildConductor(d1);

        await conductor.StartAsync(CancellationToken.None);
        await conductor.StartAsync(CancellationToken.None);
        lifetime.FireStarted();

        // First call registers the startup callback; second returns immediately (idempotency guard).
        await factory.GetClient("alpha").Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await conductor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_DisconnectsAllClients()
    {
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var (conductor, factory, _, _, lifetime) = BuildConductor(d1);

        await conductor.StartAsync(CancellationToken.None);
        lifetime.FireStarted();
        await conductor.StopAsync(CancellationToken.None);

        await factory.GetClient("alpha").Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitialConnectFails_RetryTimerEventuallyConnects()
    {
        // lock: when the post-start connect fails AND state stays Disconnected, the retry timer
        // eventually re-invokes ConnectAsync; on success the client transitions to Connected.
        var d1 = new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") };
        var opts = new DashboardOptions { Services = [d1] };
        var registry = new StaticServiceRegistry(Options.Create(opts));
        var factory = Substitute.For<IUKBatchClientFactory>();
        var client = Substitute.For<IUKBatchClient>();
        client.Service.Returns(d1);
        client.State.Returns(UKBatchClientState.Disconnected);
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
        var lifetime = new FakeHostLifetime();
        // 1-second tick interval (FakeTimeProvider drives the timer deterministically).
        var conductor = new UKBatchServiceConductor(factory, registry, lifetime, NullLogger<UKBatchServiceConductor>.Instance, clock, TimeSpan.FromSeconds(1));
        await conductor.StartAsync(CancellationToken.None);
        lifetime.FireStarted();

        // After the post-start connect: 1 attempt that failed. State stayed Disconnected.
        await WaitForConditionAsync(() => Volatile.Read(ref connectCalls) >= 1, TimeSpan.FromSeconds(2));
        connectCalls.Should().Be(1);

        // Drive the retry timer one tick — Conductor calls ConnectAsync again.
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(() => Volatile.Read(ref connectCalls) >= 2, TimeSpan.FromSeconds(2));

        connectCalls.Should().BeGreaterOrEqualTo(2, "the retry timer must re-invoke ConnectAsync on Disconnected clients.");
        await conductor.StopAsync(CancellationToken.None);
    }

    /// <summary>Polls a condition with a short cap; the connect callback / retry loop run off the calling thread.</summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    /// <summary>Test host lifetime: <see cref="FireStarted"/> cancels ApplicationStarted to simulate the host finishing startup.</summary>
    private sealed class FakeHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void FireStarted() => _started.Cancel();
        public void Dispose() => _started.Dispose();
    }
}
