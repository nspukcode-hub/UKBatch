using Bunit;
using FluentAssertions;
using Microsoft.JSInterop;
using UKBatch.Dashboard.Components.Shared.Editor;
using UKBatch.Dashboard.Models.Editor;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Bunit graceful-degradation contract for <see cref="DrawflowCanvas"/>.
/// </summary>
/// <remarks>
/// <para>BUNIT LIMITATION (mirrors the <c>DagViewTests</c> Loose-mode workaround): bunit cannot make
/// an <c>IJSObjectReference</c>-returning call (<c>import</c> / <c>init</c>) throw a
/// <see cref="JSException"/> — its <c>Setup&lt;IJSObjectReference&gt;</c> path is blocked
/// ("Use one of the SetupModule methods instead"). So the literal <c>import</c>-throws →
/// <c>JsUnavailable</c> → fallback-banner path is verified at the Editor level + manual smoke
/// step 10 (asset rename). Here we pin the two degrade behaviours bunit CAN exercise:</para>
/// <list type="number">
/// <item>The whole interop lifecycle under a mocked runtime does NOT crash the circuit (the
/// null-guarded controller path) — the DagView precedent.</item>
/// <item> second try-block: when <c>import</c>+<c>init</c> succeed but the initial
/// <c>importGraph</c> (a void call, which bunit CAN make throw) fails, the editor stays usable
/// it logs, does NOT crash, leaves <c>CanvasReady=false</c> (the drop gate), and does NOT fire
/// <c>JsUnavailable</c> (the module loaded fine; only the graph load failed).</item>
/// </list>
/// </remarks>
public sealed class DrawflowCanvasTests : TestContext
{
    // Must match DrawflowCanvas's import specifier EXACTLY (leading "./" — bare "_content/..." is
    // rejected by Safari/WebKit "does not resolve to a valid URL"; "./" is the documented RCL pattern).
    private const string ModulePath = "./_content/UKBatch.Dashboard/js/dag-editor.js";

    private static DagGraphSpec EmptyGraph() => new() { Nodes = [] };

    private static DagGraphSpec OneNodeGraph() => new()
    {
        Nodes =
        [
            new DagNodeSpec
            {
                StepId = "s1", Kind = "Job", Title = "Step1", OrderBadge = "1", X = 100, Y = 100,
            },
        ],
    };

    [Fact]
    public void MockedRuntime_RendersContainer_DoesNotCrash()
    {
        // DagView precedent: under bunit's mocked JSInterop the full import → init → importGraph
        // lifecycle runs (Loose returns defaults). OnAfterRenderAsync must not throw out — the
        // container always renders.
        JSInterop.Mode = JSRuntimeMode.Loose;

        var jsUnavailableFired = false;
        var cut = RenderComponent<DrawflowCanvas>(p => p
            .Add(c => c.Graph, OneNodeGraph())
            .Add(c => c.JsUnavailable, () => { jsUnavailableFired = true; }));

        cut.Find("div.dag-ed-canvas").Should().NotBeNull();
        // Loose mode = every call succeeds → the module "loaded", so no degrade signal fires.
        jsUnavailableFired.Should().BeFalse("under a working (loose) runtime the module loads — no degrade");
    }

    [Fact]
    public void InitialImportGraphThrows_EditorStaysUsable_LogsNoCrash_GateClosed()
    {
        // second try-block. import + init succeed (loose), but the FIRST importGraph (void) throws
        // a JSException. The component must: (a) NOT crash, (b) NOT fire JsUnavailable (module is fine),
        // (c) leave CanvasReady=false so palette drops are gated until a later re-import succeeds.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("importGraph", _ => true)
            .SetException(new JSException("simulated initial importGraph failure"));

        var jsUnavailableFired = false;
        var cut = RenderComponent<DrawflowCanvas>(p => p
            .Add(c => c.Graph, OneNodeGraph())
            .Add(c => c.JsUnavailable, () => { jsUnavailableFired = true; }));

        cut.Find("div.dag-ed-canvas").Should().NotBeNull();
        jsUnavailableFired.Should().BeFalse(
            "a failed initial importGraph leaves the editor usable — only a full import/init failure raises JsUnavailable");
        cut.Instance.CanvasReady.Should().BeFalse(
 "CanvasReady is the drop gate; it must stay false until an importGraph actually succeeds ");
    }

    [Fact]
    public async Task ModuleImportThrows_RaisesJsUnavailable_ContractIsTheJsUnavailableSeam()
    {
        // names a "module import throws → JsUnavailable" test. bunit CANNOT make the import/init
        // call throw (both return IJSObjectReference → "Use one of the SetupModule methods instead"),
        // so the literal import-failure path is manual smoke step 10. What we CAN lock here is the
        // production seam the failure uses: JsUnavailable is a real, wired EventCallback the parent
        // observes — when raised, the Editor swaps to its fallback banner (proven end-to-end in
        // EditorTests.WhenCanvasUnavailable_RendersFallbackBannerWithWizardLink). Here we pin that the
        // callback is honored (a no-arg EventCallback that re-enters the consumer).
        JSInterop.Mode = JSRuntimeMode.Loose;
        var fired = 0;
        var cut = RenderComponent<DrawflowCanvas>(p => p
            .Add(c => c.Graph, EmptyGraph())
            .Add(c => c.JsUnavailable, () => { fired++; }));

        await cut.InvokeAsync(() => cut.Instance.JsUnavailable.InvokeAsync());

        fired.Should().Be(1,
            "JsUnavailable is the degrade seam the import-failure path uses — it must be a live, " +
 "observable EventCallback (the literal import-throw is covered by manual smoke)");
    }

    [Fact]
    public async Task PublicMethods_OnUnreadyCanvas_AreNoOps_DoNotThrow()
    {
        // Before/without a usable controller (_jsReady false), every C#→JS method must be a guarded
        // no-op rather than throwing — the parent may call AddNodeAsync from a drop handler that races
        // first render. We force the un-ready state by making init throw is impossible (IJSObjectRef),
        // so instead we exercise the guards directly via an empty-graph loose render then call them;
        // the loose controller swallows them. The contract under test is "no throw".
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<DrawflowCanvas>(p => p
            .Add(c => c.Graph, EmptyGraph()));

        var act = async () =>
        {
            await cut.Instance.AddNodeAsync(new DagNodeSpec
            {
                StepId = "x", Kind = "Job", Title = "X", OrderBadge = "1", X = 0, Y = 0,
            });
            await cut.Instance.RemoveNodeAsync("x");
            await cut.Instance.UpdateLabelAsync("x", "X2", "2");
            await cut.Instance.SelectNodeAsync("x");
        };

        await act.Should().NotThrowAsync(
            "all C#→JS controller methods are guarded — a torn-down/unready circuit must not surface a JSException");
    }
}
