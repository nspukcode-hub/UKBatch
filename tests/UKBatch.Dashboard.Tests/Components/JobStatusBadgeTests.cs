using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Components.Shared;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Components;

public sealed class JobStatusBadgeTests : TestContext
{
    [Fact]
    public void Pending_RendersPendingCssClass()
    {
        var cut = RenderComponent<JobStatusBadge>(p => p.Add(b => b.Status, JobStatus.Pending));

        cut.Markup.Should().Contain("status-badge--pending");
        cut.Markup.Should().Contain("PENDING");
        cut.Markup.Should().Contain("schedule");
    }

    [Fact]
    public void Running_RendersRunningCssClassAndIcon()
    {
        var cut = RenderComponent<JobStatusBadge>(p => p.Add(b => b.Status, JobStatus.Running));

        cut.Markup.Should().Contain("status-badge--running");
        cut.Markup.Should().Contain("RUNNING");
        cut.Markup.Should().Contain("progress_activity");
    }

    [Fact]
    public void Failed_RendersFailedCssClassAndErrorIcon()
    {
        var cut = RenderComponent<JobStatusBadge>(p => p.Add(b => b.Status, JobStatus.Failed));

        cut.Markup.Should().Contain("status-badge--failed");
        cut.Markup.Should().Contain("FAILED");
        cut.Markup.Should().Contain(">error</span>");
    }
}
