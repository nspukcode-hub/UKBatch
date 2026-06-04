using FluentAssertions;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// source-grep companions for CSS facts bunit cannot assert.
/// <para>
/// bunit renders class STRINGS but never applies a real stylesheet, so it cannot prove
/// (a) the live edge status stroke wins over the kind stroke via SOURCE ORDER (equal specificity),
/// or (b) the offline worker badge uses the FAILED token, not a grey one. Per the
/// <c>feedback_bunit_closure_false_green_source_grep</c> rule these get a source-grep guard.
/// </para>
/// </summary>
public sealed class DashboardCssSourceGrepTests
{
    private static string ReadDashboardFile(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { LocateRepoRoot(), "src", "UKBatch.Dashboard" }.Concat(relativeParts).ToArray());
        File.Exists(path).Should().BeTrue($"expected dashboard file at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void DagEdgeStatusRules_DefinedAfterKindRules_SoStatusStrokeWins()
    {
        // The status stroke must beat the kind stroke. Equal specificity → source order
        // decides → the .dag-edge--running/completed/failed/cancelled block MUST appear AFTER
        // .dag-edge--on-failure (the kind rule that also sets a stroke).
        var css = ReadDashboardFile("wwwroot", "css", "dashboard.css");

        var onFailureIdx = css.IndexOf(".dag-edge--on-failure", StringComparison.Ordinal);
        var runningIdx = css.IndexOf(".dag-edge--running", StringComparison.Ordinal);
        var completedIdx = css.IndexOf(".dag-edge--completed", StringComparison.Ordinal);
        var failedIdx = css.IndexOf(".dag-edge--failed", StringComparison.Ordinal);
        var cancelledIdx = css.IndexOf(".dag-edge--cancelled", StringComparison.Ordinal);

        onFailureIdx.Should().BeGreaterThan(0, ".dag-edge--on-failure kind rule must exist");
        runningIdx.Should().BeGreaterThan(onFailureIdx, "status stroke wins by source order (after kind)");
        completedIdx.Should().BeGreaterThan(onFailureIdx);
        failedIdx.Should().BeGreaterThan(onFailureIdx);
        cancelledIdx.Should().BeGreaterThan(onFailureIdx);

        // The status rules use the shared status tokens (not hardcoded hex).
        css.Should().Contain(".dag-edge--running { stroke: var(--color-status-running); }");
        css.Should().Contain(".dag-edge--completed { stroke: var(--color-status-completed); }");
        css.Should().Contain(".dag-edge--failed { stroke: var(--color-status-failed); }");
        css.Should().Contain(".dag-edge--cancelled { stroke: var(--color-status-cancelled); }");
    }

    [Fact]
    public void WorkerOfflineBadge_UsesFailedStatusToken_NotGrey()
    {
        // The offline badge reads RED — it must reference --color-status-failed and must
        // NOT fall back to the muted --color-text-tertiary token. The .worker-badge* rules live
        // in the GLOBAL dashboard.css (moved out of the Workers.razor.css scoped bundle, since a
        // scoped-bundle skew left the badge unstyled).
        var css = ReadDashboardFile("wwwroot", "css", "dashboard.css");

        var offlineIdx = css.IndexOf(".worker-badge--offline", StringComparison.Ordinal);
        offlineIdx.Should().BeGreaterThan(0, ".worker-badge--offline rule must exist");

        // Slice the offline rule body so the assertion is scoped (the online rule legitimately uses
        // --color-status-completed elsewhere in the file).
        var braceOpen = css.IndexOf('{', offlineIdx);
        var braceClose = css.IndexOf('}', braceOpen);
        var offlineBody = css.Substring(braceOpen, braceClose - braceOpen);

        offlineBody.Should().Contain("--color-status-failed",
 "an offline/stale worker is a fault state — the badge reads red");
        offlineBody.Should().NotContain("--color-text-tertiary",
 "the offline badge no longer uses the muted grey token");
    }

    [Fact]
    public void InNodeApprove_HiddenByDefault_ShownOnlyWhenPending()
    {
        // the in-node Approve button is always in the (Approval) node DOM, but it must be
        // HIDDEN until the node carries data-pending="true" (set by the JS setPending push from
        // _awaitingGates). bunit can't apply a stylesheet, so the visibility gating is a source-grep lock.
        var css = ReadDashboardFile("wwwroot", "css", "dashboard.css");

        // Default-hidden base rule.
        css.Should().Contain(".dag-st-approve { display: none; }",
            "the in-node Approve button is hidden by default (revealed only on a pending gate)");

        // The reveal is gated on data-pending — NOT on data-status (AwaitingApproval maps to 'running').
        var revealIdx = css.IndexOf(".dag-st-node[data-pending=\"true\"] .dag-st-approve", StringComparison.Ordinal);
        revealIdx.Should().BeGreaterThan(0,
            "the Approve button is shown ONLY under.dag-st-node[data-pending=\"true\"] — keyed off the explicit " +
            "pending flag, never off data-status (AwaitingApproval is visually 'running')");

        // Slice the reveal rule body and confirm it actually turns the button visible.
        var braceOpen = css.IndexOf('{', revealIdx);
        var braceClose = css.IndexOf('}', braceOpen);
        var revealBody = css.Substring(braceOpen, braceClose - braceOpen);
        revealBody.Should().Contain("display:",
            "the data-pending reveal rule sets display (un-hides the button)");
        revealBody.Should().NotContain("display: none",
            "the data-pending rule REVEALS the button, it must not keep it hidden");
    }

    private static string LocateRepoRoot()
    {
        var assemblyPath = typeof(DashboardCssSourceGrepTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not locate UKBatch.sln in any parent directory.");
        return dir.FullName;
    }
}
