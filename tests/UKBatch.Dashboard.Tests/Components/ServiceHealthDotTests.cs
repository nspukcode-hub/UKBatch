using Bunit;
using FluentAssertions;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Components.Shared;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Components;

public sealed class ServiceHealthDotTests : TestContext
{
    [Theory]
    [InlineData(UKBatchClientState.Connected, "service-health-dot--connected")]
    [InlineData(UKBatchClientState.Connecting, "service-health-dot--reconnecting")]
    [InlineData(UKBatchClientState.Reconnecting, "service-health-dot--reconnecting")]
    [InlineData(UKBatchClientState.PartiallyConnected, "service-health-dot--partial")]
    [InlineData(UKBatchClientState.Disconnected, "service-health-dot--disconnected")]
    public void State_MapsToCssModifier(UKBatchClientState state, string expectedClass)
    {
        var cut = RenderComponent<ServiceHealthDot>(p => p.Add(d => d.State, state));
        cut.Markup.Should().Contain(expectedClass);
    }

    [Fact]
    public void Reconnecting_AddsPulseAnimation()
    {
        var cut = RenderComponent<ServiceHealthDot>(p => p.Add(d => d.State, UKBatchClientState.Reconnecting));
        cut.Markup.Should().Contain("pulse-animation");
    }

    [Fact]
    public void Connected_DoesNotPulse()
    {
        var cut = RenderComponent<ServiceHealthDot>(p => p.Add(d => d.State, UKBatchClientState.Connected));
        cut.Markup.Should().NotContain("pulse-animation");
    }

    [Fact]
    public void PartiallyConnected_AmberColorAndNoPulse()
    {
        // PartiallyConnected sits between Connected (green) and Reconnecting (amber+pulse).
        // It SHOULD be amber but NOT pulse (steady state, operator must act).
        var cut = RenderComponent<ServiceHealthDot>(p => p.Add(d => d.State, UKBatchClientState.PartiallyConnected));
        cut.Markup.Should().Contain("service-health-dot--partial");
        cut.Markup.Should().NotContain("pulse-animation");
    }
}
