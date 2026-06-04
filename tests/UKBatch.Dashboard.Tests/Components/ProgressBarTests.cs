using Bunit;
using FluentAssertions;
using UKBatch.Dashboard.Components.Shared;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Components;

public sealed class ProgressBarTests : TestContext
{
    [Fact]
    public void Renders4Segments()
    {
        var cut = RenderComponent<ProgressBar>(p => p
            .Add(b => b.Processed, 50)
            .Add(b => b.Failed, 10)
            .Add(b => b.Total, 100L));

        cut.Markup.Should().Contain("progress-bar__segment--succeeded");
        cut.Markup.Should().Contain("progress-bar__segment--failed");
        cut.Markup.Should().Contain("progress-bar__segment--remaining");
    }

    [Fact]
    public void ZeroTotal_RendersEmpty()
    {
        var cut = RenderComponent<ProgressBar>(p => p
            .Add(b => b.Processed, 0)
            .Add(b => b.Failed, 0)
            .Add(b => b.Total, (long?)null));

        cut.Markup.Should().Contain("progress-bar__segment--succeeded");
        cut.Markup.Should().Contain("width: 0%");
    }

    [Fact]
    public void CounterText_UsesMonoFontClass()
    {
        var cut = RenderComponent<ProgressBar>(p => p
            .Add(b => b.Processed, 42)
            .Add(b => b.Failed, 0)
            .Add(b => b.Total, 100L));

        cut.Markup.Should().Contain("class=\"mono\"");
        cut.Markup.Should().Contain("42");
        cut.Markup.Should().Contain("100");
    }
}
