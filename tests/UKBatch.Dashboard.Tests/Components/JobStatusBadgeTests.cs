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

    [Fact]
    public void Skipped_RendersSkippedCssClassAndIcon()
    {
        // A skipped step (a losing decision branch, or a run-if guard that did not hold) has its own badge
        // arm — previously it fell through to the generic "pending" class + "help" icon.
        var cut = RenderComponent<JobStatusBadge>(p => p.Add(b => b.Status, JobStatus.Skipped));

        cut.Markup.Should().Contain("status-badge--skipped");
        cut.Markup.Should().Contain("SKIPPED");
        cut.Markup.Should().Contain("skip_next");
        cut.Markup.Should().NotContain(">help</span>", "Skipped no longer falls through to the default help icon");
    }
}
