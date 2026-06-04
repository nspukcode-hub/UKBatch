using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Builders;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// <see cref="IDispatcherProbe"/> + <see cref="IWatchBackpressureProbe"/> contract.
/// </summary>
public class DispatcherProbeTests
{
    [Fact]
    public void IDispatcherProbe_ResolvesToJobDispatcherProbe()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddUKBatch(b => b
                    .UseInMemoryStorage()
                    .UseInProcessTransport());
            })
            .Build();
        var probe = host.Services.GetRequiredService<IDispatcherProbe>();
        probe.Should().NotBeNull();
        probe.DispatcherChannelCapacity.Should().BeGreaterThan(0);
        probe.BackpressureWaiterCount.Should().Be(0);
    }

    [Fact]
    public void IWatchBackpressureProbe_NotRegisteredInV01()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddUKBatch(b => b
                    .UseInMemoryStorage()
                    .UseInProcessTransport());
            })
            .Build();
        // pre-declared interface, no concrete in v0.1 — DI returns null.
        var probe = host.Services.GetService<IWatchBackpressureProbe>();
        probe.Should().BeNull();
    }
}
