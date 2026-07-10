using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// Source-grep invariants for the read-only status canvas's JS-interop
/// seam (<c>dag-status.js</c> + <c>DagStatusCanvas.razor</c>) and a parity guard on the status-token
/// switches. These are the production-required invariants bunit CANNOT exercise (mocked JSInterop never
/// fires the real delegated click, never loads the real UMD bundle). A grep is the only lock.
/// </summary>
/// <remarks>
/// Source-root resolution mirrors <c>DragEditorSourceInvariantTests.ResolveRepoPath</c> /
/// <c>DashboardPackageInvariants.LocateRepoRoot</c>: walk up from <see cref="AppContext.BaseDirectory"/>
/// until a directory containing <c>UKBatch.sln</c> is found.
/// </remarks>
public sealed class DagStatusSourceInvariantTests
{
    private const string DagStatusJsRelativePath =
        "src/UKBatch.Dashboard/wwwroot/js/dag-status.js";

    private const string DagStatusCanvasRelativePath =
        "src/UKBatch.Dashboard/Components/Shared/DagStatusCanvas.razor";

    private const string DagStatusClassesRelativePath =
        "src/UKBatch.Dashboard/Models/DagStatus/DagStatusClasses.cs";

    private const string DagViewRelativePath =
        "src/UKBatch.Dashboard/Components/Shared/DagView.razor";

    private static readonly string[] ForbiddenPointerHandlers =
    {
        "@onmousemove", "@onpointermove", "@ondragover", "@ondrag", "@ondrop", "@ondragstart",
    };

    // The in-node Approve adds a SECOND discrete JS→C# callback (OnApproveClickedFromJs),
    // routed through the SAME delegated container click listener. These are the ONLY two committed
    // callbacks — any third invokeMethodAsync target is a leak.
    private static readonly string[] CommittedCallbacks = { "OnNodeSelectedFromJs", "OnApproveClickedFromJs" };

    // ── read-only mode + selection seam ──────────────────────────────────────────

    [Fact]
    public void DagStatusJs_ReadOnlyFixedMode()
    {
        // The canvas is read-only. editor_mode MUST be 'fixed' (pan + zoom; NO node drag, NO
        // connection edit). A regression to 'edit' would re-enable mutation on a viewer.
        var js = File.ReadAllText(ResolveRepoPath(DagStatusJsRelativePath));

        js.Should().MatchRegex(@"editor_mode\s*=\s*'fixed'",
            "the read-only canvas MUST set editor_mode='fixed' (pan/zoom only, no mutation)");
    }

    [Fact]
    public void DagStatusJs_SelectionViaDelegatedClick()
    {
        // 'fixed'-mode click early-returns BEFORE Drawflow dispatches its own
        // nodeSelected event, so editor.on('nodeSelected') is DEAD. Selection MUST go through a delegated
        // container click → closest('.drawflow-node') → idMap → stepId → OnNodeSelectedFromJs.
        var js = File.ReadAllText(ResolveRepoPath(DagStatusJsRelativePath));

        js.Should().Contain("closest('.drawflow-node')",
            "selection uses a delegated container click → closest('.drawflow-node') (Drawflow's own " +
            "node-select event never fires in 'fixed' mode)");
        js.Should().Contain("OnNodeSelectedFromJs",
            "the delegated click invokes the single committed JS→C# callback OnNodeSelectedFromJs");
        js.Should().Contain("addEventListener('click'",
            "the delegated selection listener is a container-level click listener");

        // NO live Drawflow node-select subscription. We assert on the REGISTRATION FORM (a real
        // `editor.on('nodeSelected'` call) — NOT a bare substring — because the module's comments
        // deliberately discuss the dead event without ever writing the registration literal.
        js.Should().NotMatchRegex(@"\.on\(\s*['""]nodeSelected['""]",
 "MUST NOT register editor.on('nodeSelected') — it is dead in 'fixed' mode; the comments " +
            "may discuss it but no live registration may exist");
    }

    [Fact]
    public void DagStatusJs_NoMutationHandlers()
    {
        // A read-only viewer registers NO drop / dragover handlers. We assert on the
        // REGISTRATION FORM (addEventListener('drop'/'dragover') or editor.on('...')) rather than a
        // bare substring, so a comment mentioning the words doesn't trip the guard.
        var js = File.ReadAllText(ResolveRepoPath(DagStatusJsRelativePath));

        js.Should().NotMatchRegex(@"addEventListener\(\s*['""]drop['""]",
            "read-only canvas registers NO 'drop' handler");
        js.Should().NotMatchRegex(@"addEventListener\(\s*['""]dragover['""]",
            "read-only canvas registers NO 'dragover' handler");
        js.Should().NotMatchRegex(@"\.on\(\s*['""](nodeMoved|connectionCreated|connectionRemoved)['""]",
            "read-only canvas subscribes to NO Drawflow mutation events (nodeMoved/connectionCreated/connectionRemoved)");

        // Positive lock: the ONLY JS→C# callbacks are selection + approve. No other invokeMethodAsync target.
        var calls = Regex.Matches(js, @"invokeMethodAsync\(\s*['""](?<name>[A-Za-z0-9_]+)['""]")
            .Select(m => m.Groups["name"].Value)
            .Distinct()
            .ToArray();
        calls.Should().BeEquivalentTo(CommittedCallbacks,
            "the read-only canvas invokes EXACTLY two .NET callbacks (node-select + in-node approve) — any " +
            $"other is a leak. Found: {string.Join(", ", calls)}");
    }

    [Fact]
    public void DagStatusJs_InNodeApprove_ClickBranchOrderedBeforeNodeSelect()
    {
        // The delicate part: the delegated container click MUST check the .dag-st-approve
        // button FIRST and `return` before the generic node-select invoke — otherwise a button click both
        // approves AND falls through to node-select (double-fire), or worse, never approves. bunit can't
        // render the JS-built button or fire the real DOM click, so source order is the only lock.
        var js = File.ReadAllText(ResolveRepoPath(DagStatusJsRelativePath));

        var approveBranchIdx = js.IndexOf("closest('.dag-st-approve')", StringComparison.Ordinal);
        var approveInvokeIdx = js.IndexOf("OnApproveClickedFromJs", StringComparison.Ordinal);
        var selectInvokeIdx = js.IndexOf("OnNodeSelectedFromJs", StringComparison.Ordinal);

        approveBranchIdx.Should().BeGreaterThan(0,
            "the delegated click MUST guard on closest('.dag-st-approve') to detect the in-node Approve button");
        approveInvokeIdx.Should().BeGreaterThan(approveBranchIdx,
            "the approve invoke is inside the .dag-st-approve branch");
        approveInvokeIdx.Should().BeLessThan(selectInvokeIdx,
            "the in-node Approve branch (OnApproveClickedFromJs) MUST come BEFORE the generic node-select " +
            "branch (OnNodeSelectedFromJs) so a button click approves and returns — never double-fires");
    }

    [Fact]
    public void DagStatusJs_InNodeApprove_ButtonRenderedAndFlaggedByPending()
    {
        // The button is in the node template (Approval nodes only) and is revealed by a data-pending flag
        // that a dedicated setPending push sets — NOT inferred from data-status (AwaitingApproval maps to
        // "running"). Lock the template literal + the setPending seam + the data-pending write.
        var js = File.ReadAllText(ResolveRepoPath(DagStatusJsRelativePath));

        js.Should().Contain("dag-st-approve",
            "the in-node Approve button is rendered in the node template");
        js.Should().Contain("setPending",
            "a dedicated setPending push carries the pending-gate set C#→JS (not via data-status)");
        js.Should().MatchRegex(@"dataset\.pending\s*=\s*'true'",
            "setPending sets data-pending='true' on pending gate nodes (CSS reveals the Approve button off that flag)");
    }

    [Fact]
    public void DagStatusJs_LazyLoadsVendoredDrawflowUmdViaClassicScript()
    {
        // Same lesson as dag-editor.js: the vendored Drawflow is UMD-only (no ESM dist for 0.0.60). A
        // static ESM `import` of the UMD bundle is unreliable cross-browser (top-level `this` undefined
        // under ESM strict → globalThis.Drawflow left unset). MUST be a classic <script> inject.
        var js = File.ReadAllText(ResolveRepoPath(DagStatusJsRelativePath));

        js.Should().NotContain("import '../lib/drawflow/drawflow.min.js'",
            "a UMD bundle must NOT be loaded via a static ESM import (unreliable cross-browser)");
        js.Should().Contain("createElement('script')",
            "Drawflow (UMD) must be loaded by injecting a classic <script>, not a static ESM import");
        js.Should().Contain("globalThis.Drawflow",
            "dag-status.js reads the Drawflow constructor off the global after the classic-script load");
    }

    [Fact]
    public void DagStatusCanvas_ImportSpecifierIsContentRelative()
    {
        // CRITICAL (Safari lesson): the RCL JS module import MUST be "./_content/..."
        // a bare "_content/..." specifier is rejected by Safari/WebKit even though Chrome tolerates it.
        var razor = File.ReadAllText(ResolveRepoPath(DagStatusCanvasRelativePath));

        razor.Should().Contain("\"./_content/UKBatch.Dashboard/js/dag-status.js\"",
            "the module import specifier MUST be content-relative \"./_content/...\" (Safari/WebKit rejects bare \"_content/...\")");
        razor.Should().NotMatchRegex(@"import""\s*,\s*""_content/",
            "MUST NOT import a bare \"_content/...\" specifier (Safari/WebKit 'does not resolve to a valid URL')");
    }

    [Fact]
    public void DagStatusCanvas_ContainerHasNoBlazorPointerHandlers()
    {
        // The canvas container carries NO Blazor pointer handlers (each is a per-event
        // SignalR round-trip → UI freeze). Pan/zoom is Drawflow-native + the three discrete toolbar
        // buttons. (The toolbar's @onclick on the three buttons IS allowed — discrete, not per-pixel.)
        var razor = File.ReadAllText(ResolveRepoPath(DagStatusCanvasRelativePath));

        foreach (var bad in ForbiddenPointerHandlers)
        {
            razor.Should().NotContain(bad,
                $"DagStatusCanvas MUST carry NO Blazor pointer handler '{bad}' (SignalR flood); pan/zoom is Drawflow-native");
        }
    }

    // ── parity guard: DagStatusClasses.StatusToken ⟷ DagView.StatusClass ─────────

    [Fact]
    public void StatusTokenParity_DagStatusClassesAndDagView_EnumerateSameFamilies()
    {
        // Code-review NICE→promote: DagStatusClasses.StatusToken is a verbatim port of DagView's status
        // switches. A future JobStatus addition (or a re-grouping of an existing one) must update BOTH
        // switches or the live canvas silently desyncs from the static Detail view. We can't reflect into
        // private switch arms, so we grep the source of each and assert they enumerate the SAME set of
        // JobStatus families. (The class-name prefix differs — "muted"/"running" vs "dag-node--muted"/
        // "dag-node--running" — so we compare the JobStatus.* names referenced, not the literal tokens.)
        var statusClasses = File.ReadAllText(ResolveRepoPath(DagStatusClassesRelativePath));
        var dagView = File.ReadAllText(ResolveRepoPath(DagViewRelativePath));

        var canvasFamilies = JobStatusFamilies(statusClasses);
        var dagViewFamilies = JobStatusFamilies(dagView);

        canvasFamilies.Should().NotBeEmpty("the status switch must reference JobStatus families");
        canvasFamilies.Should().BeEquivalentTo(dagViewFamilies,
            "DagStatusClasses.StatusToken and DagView.StatusClass/EdgeStatusClass MUST enumerate the SAME " +
            "JobStatus → token families — a divergence means the live canvas paints a status the static view " +
            "doesn't (or vice versa), the exact silent-desync this parity guard exists to kill. " +
            $"Canvas: [{string.Join(", ", canvasFamilies.OrderBy(x => x))}] " +
            $"DagView: [{string.Join(", ", dagViewFamilies.OrderBy(x => x))}]");
    }

    // Extracts the DISTINCT set of JobStatus.<Name> tokens referenced anywhere in a file's status
    // switch arms. (Both files reference them as `JobStatus.Running or JobStatus.Retrying...`.)
    private static HashSet<string> JobStatusFamilies(string source)
        => Regex.Matches(source, @"JobStatus\.(?<name>[A-Za-z]+)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void DagStatus_CompensationKindToken_LockedInEdgesAndCanvas()
    {
        // The "Compensation" node/edge kind is a rendering contract: the CSS selectors key off the
        // lowercased kind token, and the canvas synthesizes compensator nodes with it. Renaming the
        // token in one place silently unstyles the compensation lane, so lock it at both emit sites.
        var edges = File.ReadAllText(ResolveRepoPath("src/UKBatch.Dashboard/Models/DagStatus/DagStatusEdges.cs"));
        edges.Should().Contain("\"Compensation\"",
            "the compensation edge kind token feeds the dashed parent-to-compensator styling");

        var canvas = File.ReadAllText(ResolveRepoPath(DagStatusCanvasRelativePath));
        canvas.Should().Contain("Compensation",
            "the canvas must synthesize compensator nodes with the Compensation kind (and list them in the zero-JS fallback)");
    }

    // ── source-root resolution (mirrors DragEditorSourceInvariantTests) ──────────

    private static string ResolveRepoPath(string relativePath)
    {
        var root = FindSolutionRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "Could not locate UKBatch.sln by walking up from " + AppContext.BaseDirectory);
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string? FindSolutionRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
