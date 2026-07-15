using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// Source-grep invariants for the drag-drop editor's JS-interop seam. These are the SignalR-flood
/// guards: invariants that are production-required but that bunit CANNOT exercise (a mocked
/// JSInterop never fires the real pointer events). A grep is the only lock.
/// </summary>
/// <remarks>
/// <para>Scope: the drag-drop editor's JS-interop files — <c>dag-editor.js</c>,
/// <c>DrawflowCanvas.razor</c>, and the no-Blazor-pointer-handler grep over <c>Editor.razor</c> /
/// <c>DagPalette.razor</c> / <c>DagOrderRail.razor</c> (the helper scans every Editor component that
/// exists, so it tightens automatically).</para>
/// <para>Source-root resolution mirrors <c>SampleSourceGuardTests.FindSolutionRoot</c> /
/// <c>DashboardPackageInvariants.LocateRepoRoot</c>: walk up from <see cref="AppContext.BaseDirectory"/>
/// until a directory containing <c>UKBatch.sln</c> is found.</para>
/// </remarks>
public sealed class DragEditorSourceInvariantTests
{
    private const string DagEditorJsRelativePath =
        "src/UKBatch.Dashboard/wwwroot/js/dag-editor.js";

    private const string DrawflowCanvasRelativePath =
        "src/UKBatch.Dashboard/Components/Shared/Editor/DrawflowCanvas.razor";

    private const string EditorComponentsDir =
        "src/UKBatch.Dashboard/Components/Shared/Editor";

    private const string WizardRelativePath =
        "src/UKBatch.Dashboard/Components/Pages/Batches/Wizard.razor";

    private const string EditorRelativePath =
        "src/UKBatch.Dashboard/Components/Pages/Batches/Editor.razor";

    private const string DagPaletteRelativePath =
        "src/UKBatch.Dashboard/Components/Shared/Editor/DagPalette.razor";

    private const string DashboardCssRelativePath =
        "src/UKBatch.Dashboard/wwwroot/css/dashboard.css";

    // The ONLY five.NET callbacks dag-editor.js is allowed to invoke. Anything else = a per-pixel
    // leak. OnNodeEditRequested is the "decouple move/edit" hover-Edit-button click — a DISCRETE
    // user action (one invoke per click), NOT a per-frame/per-pixel call, so it is fired ONLY from
    // the click handler and does NOT violate the no-flood invariant (the companion
    // DagEditorJs_HasNoPerPixelInvoke_InMoveOrDragHandlers test pins that it never lands in a
    // mousemove/pointermove/dragover handler).
    private static readonly string[] CommittedCallbacks =
    {
        "OnNodeMoved", "OnNodeSelected", "OnNodeDropped", "OnNodeRemoved", "OnNodeEditRequested",
    };

    [Fact]
    public void DagEditorJs_InvokesOnlyTheFiveCommittedCallbacks()
    {
        var js = File.ReadAllText(ResolveRepoPath(DagEditorJsRelativePath));

        // Every invokeMethodAsync('Name',...) call — extract the method name argument.
        var calls = Regex.Matches(js, @"invokeMethodAsync\(\s*['""](?<name>[A-Za-z0-9_]+)['""]")
            .Select(m => m.Groups["name"].Value)
            .ToArray();

        calls.Should().NotBeEmpty("dag-editor.js must report committed events to C#");
        calls.Should().OnlyContain(
            n => CommittedCallbacks.Contains(n),
            "dag-editor.js may ONLY invoke the 5 committed callbacks (OnNodeMoved/OnNodeSelected/" +
            "OnNodeDropped/OnNodeRemoved/OnNodeEditRequested) — any other invokeMethodAsync target is " +
            "a per-pixel SignalR-flood leak. OnNodeEditRequested is a discrete hover-Edit-button " +
            $"click, not a per-frame call. Found: {string.Join(", ", calls.Distinct())}");
    }

    [Fact]
    public void DagEditorJs_HasNoPerPixelInvoke_InMoveOrDragHandlers()
    {
        var js = File.ReadAllText(ResolveRepoPath(DagEditorJsRelativePath));

        // The high-frequency DOM handlers (dragover, and any mousemove/pointermove) must NOT carry a
        // .NET round-trip. We assert no invokeMethodAsync appears on the same physical line as a
        // mousemove/pointermove/dragover handler body. (onDrop IS allowed to invoke — it is a single
        // discrete commit, not per-frame.)
        var lines = js.Split('\n');
        foreach (var line in lines)
        {
            var mentionsHotHandler =
                line.Contains("mousemove", StringComparison.Ordinal) ||
                line.Contains("pointermove", StringComparison.Ordinal) ||
                line.Contains("'dragover'", StringComparison.Ordinal) ||
                line.Contains("\"dragover\"", StringComparison.Ordinal);

            if (mentionsHotHandler)
            {
                line.Should().NotContain("invokeMethodAsync",
                    "a high-frequency pointer/drag handler MUST NOT invoke.NET (SignalR flood). " +
                    $"Offending line: {line.Trim()}");
            }
        }

        // Positive lock: the move commit MUST be debounced via setTimeout (pointer-up granularity).
        js.Should().MatchRegex(@"setTimeout\(",
            "OnNodeMoved must be debounced (setTimeout) so a drag-burst yields one frame at settle, not per pixel");
    }

    [Fact]
    public void DagEditorJs_LazyLoadsVendoredDrawflowUmdViaClassicScript()
    {
        // The vendored Drawflow is UMD-only (no ESM dist for 0.0.60). It MUST NOT be loaded via a static
        // ESM `import`: a UMD bundle parsed as an ES module does not reliably attach to the global across
        // browsers (under ESM strict mode top-level `this` is undefined) — empirically it left
        // globalThis.Drawflow unset and degraded the editor to its fallback banner.
        // The robust path is to inject a CLASSIC <script> for the vendored file (where `self`/`this` ===
        // window so the UMD's else-branch sets window.Drawflow) and then read globalThis.Drawflow.
        var js = File.ReadAllText(ResolveRepoPath(DagEditorJsRelativePath));

        js.Should().NotContain("import '../lib/drawflow/drawflow.min.js'",
            "a UMD bundle must NOT be loaded via a static ESM import (unreliable cross-browser)");
        js.Should().Contain("../lib/drawflow/drawflow.min.js",
            "dag-editor.js must reference the vendored Drawflow path (loaded via a classic <script>)");
        js.Should().Contain("createElement('script')",
            "Drawflow (UMD) must be loaded by injecting a classic <script>, not a static ESM import");
        js.Should().Contain("globalThis.Drawflow",
            "dag-editor.js reads the Drawflow constructor off the global after the classic-script load");
    }

    [Fact]
    public void EditorComponents_HaveNoBlazorPointerHandlers()
    {
        // NO Blazor-bound pointer handlers anywhere in the Editor component tree. Each such
        // binding is a per-event SignalR round-trip → UI freeze under drag. Drawflow + plain-JS
        // listeners (inside dag-editor.js / DagPalette's literal `ondragstart=`) handle all pointer
        // work. NB: the LITERAL HTML attribute `ondragstart=` (string, zero C# round-trip) is allowed;
        // only the Blazor BINDING `@ondragstart` is forbidden.
        var forbidden = new[]
        {
            "@onmousemove", "@onpointermove", "@ondragover", "@ondrag", "@ondrop", "@ondragstart",
        };

        var dir = ResolveRepoPath(EditorComponentsDir);
        Directory.Exists(dir).Should().BeTrue($"the Editor component directory must exist at {dir}");

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var bad in forbidden)
            {
                if (text.Contains(bad, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: forbidden Blazor pointer handler '{bad}'");
                }
            }
        }

        offenders.Should().BeEmpty(
            "Editor components MUST NOT bind Blazor pointer handlers. Pointer/drag work lives " +
            "in dag-editor.js + plain-JS HTML attributes. Offenders: " + string.Join("; ", offenders));
    }

    [Fact]
    public void DrawflowCanvas_ContainerHasNoBlazorPointerHandlers()
    {
        // Targeted lock on the SOLE JS-interop component's container div. Redundant with the
        // directory scan above, but pins the specific file the contract names so a rename can't
        // silently drop the guard.
        var razor = File.ReadAllText(ResolveRepoPath(DrawflowCanvasRelativePath));

        razor.Should().NotContain("@ondragover");
        razor.Should().NotContain("@ondrop");
        razor.Should().NotContain("@onmousemove");
        razor.Should().NotContain("@ondrag",
            "DrawflowCanvas's container must carry NO Blazor pointer handlers — the JS module owns drop");
    }

    [Fact]
    public void Wizard_CompensationStep_PassesAllowTargetServiceFalse()
    {
        // HARD requirement: OnFailure compensation steps run LOCALLY and MUST NOT render the
        // cross-service target dropdown. The Wizard's compensation render passes AllowTargetService="false".
        // bunit can't guard this (it asserts rendered output; a regression would silently ADD a dropdown
        // nobody asserts the absence of) — a source-grep is the lock that StepDraftEditor.razor +
        // Wizard.razor comments both promise. A careless future edit to the OnFailure render that flips
        // this to "true" would expose a cross-service target on a compensation step — a real correctness
        // bug (compensation is local-only).
        var wizard = File.ReadAllText(ResolveRepoPath(WizardRelativePath));

        wizard.Should().MatchRegex(
            @"<StepDraftEditor[^>]*AllowTargetService=""false""",
            "compensation steps MUST pass AllowTargetService=\"false\" (compensation runs locally, " +
            "no cross-service dropdown)");

        // And the main step list passes "true" (cross-service-capable) — confirms BOTH call sites exist
        // so the grep above isn't trivially satisfied by an absent compensation render.
        wizard.Should().MatchRegex(
            @"<StepDraftEditor[^>]*AllowTargetService=""true""",
            "the main step editor passes AllowTargetService=\"true\" (top-level Job steps may run cross-service)");
    }

    [Fact]
    public void EditorComponents_EveryForeachHasKey()
    {
        // bunit-blind browser-diff lock: a `@for`/`@foreach` over a mutable/reorderable list WITHOUT
        // a `@key` makes Blazor re-use DOM by POSITION, which corrupts component state on
        // reorder/remove — a real browser bug bunit cannot reproduce (its foreach captures `n` fresh
        // per iteration). Coarse FILE-LEVEL lock: any Editor component razor that renders a loop must
        // also declare at least one `@key`. (Intentionally coarse — a per-loop check would false-trip
        // on the legitimate `<option>` foreach inside a <select>, which needs no key. A CI lock prefers
        // a slightly loose true-positive over brittle false-positives; the per-loop key correctness is
        // covered by the DagOrderRail/StepDraftEditor contract comments + manual smoke.)
        var files = new List<string> { ResolveRepoPath(EditorRelativePath) };
        files.AddRange(Directory.GetFiles(ResolveRepoPath(EditorComponentsDir), "*.razor", SearchOption.AllDirectories));

        var offenders = new List<string>();
        foreach (var file in files.Distinct())
        {
            var text = File.ReadAllText(file);
            var hasLoop = text.Contains("@foreach", StringComparison.Ordinal)
                       || text.Contains("@for ", StringComparison.Ordinal)
                       || text.Contains("@for(", StringComparison.Ordinal);
            if (!hasLoop) continue;
            if (!text.Contains("@key", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        offenders.Should().BeEmpty(
            "every Editor component that renders a loop MUST declare at least one `@key` so Blazor diffs " +
            "list items by identity, not position (reorder/remove DOM-reuse corruption — bunit-blind, " +
            "the source-grep is the only lock). Offenders (loop without any @key): " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Editor_BadgeEmission_UsesInvariantCulture()
    {
        // tr-TR guard (a comma-decimal culture turns "12.5" into "12,5" and breaks layout/identity).
        // The Editor emits 1-based order badges as strings
        // (`(i + 1).ToString(...)` / `(index + 1).ToString(...)`) crossing into the canvas. Those
        // ToString calls MUST pin CultureInfo.InvariantCulture. Positive lock + negative lock:
        var editor = File.ReadAllText(ResolveRepoPath(EditorRelativePath));

        editor.Should().Contain("CultureInfo.InvariantCulture",
            "the Editor's order-badge stringification MUST pin InvariantCulture (tr-TR comma-decimal guard)");

        // Negative lock: NO `(i + 1).ToString` / `(index + 1).ToString` WITHOUT a culture argument.
        // The regex intent: match the badge expression `(<ident> + 1).ToString(` immediately followed
        // by `)` — i.e. a bare, argument-less ToString on the 1-based index. Any culture-carrying call
        // has a non-`)` first char after the paren, so it does NOT match. Coarse/robust by design — a
        // CI lock tolerates a slightly loose pattern over a brittle one (false-negatives here are far
        // worse than a slightly loose true-positive).
        editor.Should().NotMatchRegex(
            @"\((?:i|index)\s*\+\s*1\)\.ToString\(\s*\)",
            "an order-badge ToString on the 1-based index MUST pass CultureInfo.InvariantCulture — a " +
            "bare `(i + 1).ToString` is a tr-TR comma-decimal regression waiting to happen");
    }

    // ── onFailure-canvas Bucket 3 — compensation-step canvas invariants ──────────

    [Fact]
    public void DagPalette_HasFourthCompensationTile_WithOnFailurePayload()
    {
        // The 4th palette tile mints a compensation (onFailure) step. Its dragstart sets the SAME MIME
        // key as the other tiles but the payload 'OnFailure' — dag-editor.js's onDrop reads the raw
        // string verbatim and DrawflowCanvas.OnNodeDropped routes it to a Job draft with the onFailure
        // LANE flag (it is NOT a BatchStepType). bunit can't exercise the literal HTML `ondragstart`
        // (zero C# round-trip) — a source-grep is the only lock that the tile + its payload exist.
        var palette = File.ReadAllText(ResolveRepoPath(DagPaletteRelativePath));

        palette.Should().Contain(
            "event.dataTransfer.setData('application/x-ukbatch-step','OnFailure')",
            "the 4th palette tile MUST set the 'OnFailure' drag payload on the shared MIME key");

        // Confirm the OTHER three payloads are still present so the grep above isn't satisfied by a tile
        // that replaced (rather than added to) the existing three.
        palette.Should().Contain("setData('application/x-ukbatch-step','Job')");
        palette.Should().Contain("setData('application/x-ukbatch-step','ParallelGroup')");
        palette.Should().Contain("setData('application/x-ukbatch-step','ApprovalGate')");
    }

    [Fact]
    public void Editor_CompensationModal_LocksTargetServiceOff_ViaIsSelectedOnFailure()
    {
        // HARD requirement: compensation (onFailure) steps run LOCALLY and MUST NOT render the
        // cross-service target dropdown. The Editor's modal passes
        // AllowTargetService="@(!IsSelectedOnFailure())" — false when the selected node is a compensation
        // step, mirroring the Wizard's AllowTargetService="false" compensation lock. bunit can't guard
        // this (it asserts rendered output; a regression would silently ADD a dropdown nobody asserts the
        // absence of) — a source-grep is the lock. A careless future edit that drops the guard would
        // expose a cross-service target on a compensation step — a real correctness bug.
        var editor = File.ReadAllText(ResolveRepoPath(EditorRelativePath));

        editor.Should().Contain(
            @"AllowTargetService=""@(!IsSelectedOnFailure())""",
            "the Editor modal MUST gate AllowTargetService on !IsSelectedOnFailure() (compensation " +
            "runs locally, no cross-service dropdown)");

        // And the gate predicate itself exists (so the binding isn't pointing at a deleted method).
        editor.Should().Contain("IsSelectedOnFailure",
            "the IsSelectedOnFailure() predicate backing the AllowTargetService gate must exist");
    }

    [Fact]
    public void DagEditorJs_UsesTypedEdgeSet_NotLegacyLastOrder()
    {
        // The JS edge sync moved from a positional/order-derived chain to a TYPED edge set
        // ({fromStepId,toStepId,kind}[]) so the OnFailure branch (a non-spine pairing) can be expressed.
        // The legacy `lastOrder` state (which only encoded a linear order) must be GONE — its presence
        // would mean the OnFailure edges can't be carried. Positive: the authoritative `lastEdges` set +
        // `data-kind` decode + `syncConnectionsImpl` are present.
        var js = File.ReadAllText(ResolveRepoPath(DagEditorJsRelativePath));

        js.Should().NotContain("lastOrder",
            "the legacy positional `lastOrder` state must be removed — edges are now a TYPED set " +
            "(lastEdges), which is what lets the OnFailure branch be a non-spine pairing");
        js.Should().Contain("st.lastEdges",
            "dag-editor.js remembers the authoritative TYPED edge set as st.lastEdges (the revert target)");
        js.Should().Contain("syncConnectionsImpl",
            "the typed-edge connection sync (main flow + OnFailure branch) is the syncConnectionsImpl path");
        js.Should().Contain("data-kind",
            "applyEdgeKinds tags each .connection with data-kind so CSS can paint the OnFailure branch red-dashed");
    }

    [Fact]
    public void DagEditorJs_ImportFlushesLayout_BeforeDrawingConnections()
    {
        // The onFailure node sits on a LOWER lane (~260px below the spine), so importImpl MUST force a
        // synchronous reflow (read offsetHeight) AFTER adding nodes and BEFORE syncConnectionsImpl
        // otherwise the first dashed edge anchors at the canvas TOP (height ~0, "arrows detached" bug).
        // A source-grep locks the flush stays.
        var js = File.ReadAllText(ResolveRepoPath(DagEditorJsRelativePath));

        js.Should().Contain("offsetHeight",
            "importImpl MUST read container.offsetHeight to flush layout before drawing the lower-lane " +
            "OnFailure branch (detached-arrow guard)");
    }

    [Fact]
    public void DagEditorJs_StillInvokesOnlyTheFiveCommittedCallbacks_NoNewJsInvokable()
    {
        // Re-assert the callback count holds AFTER the onFailure-canvas work: the compensation
        // feature added NO new JS→C# callback (drop routing reuses the existing OnNodeDropped path with
        // the IsOnFailure flag). A 6th invokeMethodAsync target would be a new per-event seam to audit.
        var js = File.ReadAllText(ResolveRepoPath(DagEditorJsRelativePath));

        var calls = Regex.Matches(js, @"invokeMethodAsync\(\s*['""](?<name>[A-Za-z0-9_]+)['""]")
            .Select(m => m.Groups["name"].Value)
            .Distinct()
            .ToArray();

        calls.Should().OnlyContain(n => CommittedCallbacks.Contains(n),
            "the onFailure-canvas feature must NOT add a new JS→C# callback — drop routing reuses " +
            "OnNodeDropped (IsOnFailure flag). Found: " + string.Join(", ", calls));
        calls.Length.Should().BeLessThanOrEqualTo(CommittedCallbacks.Length,
            "no new invokeMethodAsync target beyond the 5 committed callbacks");
    }

    [Fact]
    public void DashboardCss_HasOnFailureBranchAndFailureNodeRules()
    {
        // The red-dashed OnFailure branch + the dashed failure-node modifier are presentation contracts
        // the C# edge `Kind="OnFailure"` (decoded to data-kind by applyEdgeKinds) and the
        // `isOnFailure` node spec depend on. If these CSS rules are dropped/renamed, the branch renders
        // as a plain solid edge with no visual distinction — a silent UX regression bunit can't see.
        var css = File.ReadAllText(ResolveRepoPath(DashboardCssRelativePath));

        css.Should().Contain(
            @".dag-ed-canvas .connection[data-kind=""OnFailure""]",
            "the editor canvas MUST carry the OnFailure connection rule (red-dashed compensation branch)");
        css.Should().Contain("stroke-dasharray",
            "the OnFailure connection rule MUST be dashed (stroke-dasharray) to read as a compensation branch");
        css.Should().Contain(".dag-ed-node--failure",
            "the failure-node modifier (dashed red-left-border card) MUST exist for compensation nodes");
    }

    [Fact]
    public void DagEditorJs_WrapsRemoveNodeId_SoProtectedCardsCannotBeDeleted()
    {
        // A decision's branch card projects an entry of its parent's branch list — it has no entry in the C#
        // Steps list, so a canvas delete would leave the model and the canvas disagreeing. Drawflow routes
        // EVERY delete surface (the Delete key, the right-click "x", our toolbar button) through
        // removeNodeId, so wrapping that ONE method is what covers them all.
        //
        // This is a source grep because the alternative is invisible to bunit: it cannot run Drawflow, so a
        // dropped wrapper would leave every test green while a branch card silently deletes in the browser.
        var js = File.ReadAllText(ResolveRepoPath(DagEditorJsRelativePath));

        js.Should().Contain("editor.removeNodeId =",
            "the delete guard MUST wrap removeNodeId — a per-surface event guard cannot cover the right-click x, "
            + "and a keydown guard cannot win against Drawflow's own listener on the same container");
        js.Should().Contain("deleteProtected",
            "the wrapper MUST consult the protected set (populated from the node spec's isDeleteProtected)");
        js.Should().Contain("allowProtectedRemove",
            "a C#-driven remove MUST be able to bypass the refusal — the guard exists to stop an OPERATOR delete");
    }

    [Fact]
    public void DashboardCss_HasBranchPaletteAndProtectedDeleteRules()
    {
        // Colour is the ONLY thing pairing a decision's chip with its branch card and the edge between them
        // (the editor prints no text on edges), so the palette is a presentation contract, not decoration.
        var css = File.ReadAllText(ResolveRepoPath(DashboardCssRelativePath));

        css.Should().Contain("--color-branch-1",
            "the branch palette MUST exist — it is what pairs a chip with its card and edge");
        css.Should().Contain("--color-branch-else",
            "the else branch MUST have its own neutral key so the default branch reads as the fallback it is");
        css.Should().NotContain("--color-branch-1: var(--color-status-",
            "branch colours MUST NOT alias the status ramp — those mean how a RUN went, which authoring cannot show");
        css.Should().Contain(@".dag-ed-canvas .connection[data-branch=""else""]",
            "decision edges MUST resolve their colour from data-branch (stamped by applyEdgeKinds)");
        css.Should().Contain(".dag-ed-nodelete .drawflow-delete",
            "Drawflow's right-click x MUST be hidden on a protected card — the guard would otherwise refuse a visible button");
    }

    // ── source-root resolution (mirrors SampleSourceGuardTests / DashboardPackageInvariants) ──

    private static string ResolveRepoPath(string relativePath)
    {
        var root = FindSolutionRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "Could not locate UKBatch.sln by walking up from " + AppContext.BaseDirectory);
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return full;
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
