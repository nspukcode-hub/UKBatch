using System.Net;
using FluentAssertions;
using UKBatch.Dashboard.Tests.Integration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// Locks down the Drawflow editor's static assets
/// (<c>_content/UKBatch.Dashboard/lib/drawflow/*</c> + <c>js/dag-editor.js</c>). bunit's mocked
/// JSInterop CANNOT catch a runtime module/asset 404 — exactly the silent-failure mode
/// fast-follow hit with <c>blazor.web.js</c> and hit with <c>dag-canvas.js</c>. Only an
/// HttpClient asset test does. Mirrors <see cref="DagCanvasAssetRegressionTests"/>.
/// </summary>
/// <remarks>
/// <para>VENDORING NOTE (pinned to what ships): Drawflow 0.0.60's npm dist is <b>UMD-only</b> — there
/// is no ESM build. We therefore vendor the UMD (<c>lib/drawflow/drawflow.min.js</c>) and
/// <c>dag-editor.js</c> imports it for its side-effect then reads <c>globalThis.Drawflow</c>. So the
/// Drawflow asset asserts the UMD signature (<c>exports.Drawflow=t</c>), while <c>dag-editor.js</c>
/// itself IS an ES module and asserts its <c>export function</c>s.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DrawflowAssetRegressionTests : IClassFixture<SampleDashboardFactory>
{
    private readonly SampleDashboardFactory _factory;

    public DrawflowAssetRegressionTests(SampleDashboardFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DrawflowJs_IsServedAt_StaticWebAssetsPath()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/lib/drawflow/drawflow.min.js", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the vendored Drawflow UMD MUST be in the host project's static-web-assets manifest — " +
            "absence means the RCL wwwroot packaging broke or the file got renamed/removed");
    }

    [Fact]
    public async Task DrawflowJs_IsRealDrawflowUmd_NotEsm()
    {
        // Pins the actual shipped form (UMD, NOT ESM — no ESM dist exists for 0.0.60) AND that it is
        // the genuine Drawflow (not an empty/corrupt vendor). dag-editor.js's side-effect import +
        // globalThis.Drawflow read depends on this UMD global-assignment shape.
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/lib/drawflow/drawflow.min.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotBeEmpty();
        body.Should().Contain("Drawflow v0.0.60",
            "the vendored file MUST carry the pinned-version MIT vendor header");
        body.Should().Contain("exports.Drawflow=t()",
            "the vendored dist MUST be the Drawflow UMD (it assigns the constructor to the global) — " +
            "if this becomes an ESM build, dag-editor.js's side-effect-import + globalThis.Drawflow read must change");
        // Genuine Drawflow internals (guards against a stubbed/wrong vendor).
        body.Should().Contain("removeNodeId");
        body.Should().Contain("getNodeFromId");
    }

    [Fact]
    public async Task DrawflowJs_ContentTypeIsJavaScript()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/lib/drawflow/drawflow.min.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var ct = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        ct.Should().NotBeEmpty();
        ct.Contains("javascript", StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            $"drawflow.min.js MUST be served with a JavaScript MIME type (got '{ct}')");
    }

    [Fact]
    public async Task DrawflowCss_IsServedAt_StaticWebAssetsPath()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/lib/drawflow/drawflow.min.css", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Drawflow stylesheet MUST be in the static-web-assets manifest");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain(".drawflow",
            "the vendored CSS must be the real Drawflow stylesheet (its base selectors)");
    }

    [Fact]
    public async Task DagEditorJs_IsServedAt_StaticWebAssetsPath()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-editor.js", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Drawflow bridge ES module MUST be in the static-web-assets manifest — " +
            "DrawflowCanvas.razor imports it dynamically from C#");
    }

    [Fact]
    public async Task DagEditorJs_ContentTypeIsJavaScript()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-editor.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var ct = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        // ESM imports refuse text/html (route fall-through) or application/octet-stream (mime missing).
        ct.Should().NotBeEmpty();
        ct.Contains("javascript", StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            $"dag-editor.js MUST be served with a JavaScript MIME type (got '{ct}') — ESM imports refuse text/html");
    }

    [Fact]
    public async Task DagEditorJs_BodyContainsExpectedEsModuleExports()
    {
        // DrawflowCanvas.razor imports the module and invokes `init` + `dispose`. If either export
        // disappears, OnAfterRenderAsync silently catches the JSException (graceful degradation) and
        // the editor goes dead in production with no clear signal. This test pins the contract.
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-editor.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("export async function init",
            "DrawflowCanvas calls module.InvokeAsync<IJSObjectReference>(\"init\",...) — the export must exist " +
            "(async: it awaits the classic-script Drawflow load before constructing the editor)");
        body.Should().Contain("export function dispose",
            "DrawflowCanvas calls module.InvokeVoidAsync(\"dispose\", container) — the export must exist");
        body.Should().Contain("../lib/drawflow/drawflow.min.js",
            "dag-editor.js obtains the Drawflow constructor by lazily loading the vendored UMD via a classic <script>");
    }

    [Fact]
    public async Task DagEditorJs_ServedBody_EncodesQuotesInEscapeHtml()
    {
        // escapeHtml output is interpolated into QUOTED attribute contexts (title="..." / data-step="...").
        // The DOM-textContent trick alone encodes < > & but NOT " or ', so a step/job name like
        // a" onmouseover="..." would break out of the attribute and inject a live event handler. The
        // served asset MUST carry the quote-encoding replaces. bunit's mocked JSInterop never loads the
        // real module, so only an HttpClient body assertion locks this — a future simplification that
        // drops the replaces fails CI here.
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-editor.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("&quot;",
            "escapeHtml MUST encode the double quote so values stay inside quoted attributes — " +
            "its absence reopens the attribute-context injection seam");
        body.Should().Contain("&#39;",
            "escapeHtml MUST encode the single quote so values stay inside single-quoted attributes");
    }

    [Fact]
    public async Task DagEditorJs_ServedBody_CarriesTypedEdgeSync_ForOnFailureBranch()
    {
        // onFailure-canvas Bucket 4: the SERVED asset (not just the source on disk) must carry the
        // typed-edge sync that draws the OnFailure compensation branch. bunit's mocked JSInterop can't
        // catch a stale/cached/renamed asset — only an HttpClient body assertion does (
        // fast-follow lesson). Locks `lastEdges` (authoritative typed edge set) + `data-kind` (the
        // decode applyEdgeKinds writes so CSS paints the branch red-dashed).
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-editor.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("lastEdges",
            "the served dag-editor.js MUST carry the TYPED edge state (st.lastEdges) that expresses the " +
            "OnFailure branch — a stale asset would silently drop the compensation edges");
        body.Should().Contain("data-kind",
            "the served dag-editor.js MUST tag connections with data-kind so CSS paints the OnFailure " +
            "branch red-dashed (applyEdgeKinds)");
        body.Should().NotContain("lastOrder",
            "the served asset MUST NOT carry the legacy positional `lastOrder` state (replaced by the " +
            "typed edge set — its presence would mean OnFailure edges can't be carried)");
    }

    [Fact]
    public async Task DashboardLanding_DoesNotEmitDrawflowScriptTag()
    {
        // Drawflow is loaded LAZILY via dag-editor.js's side-effect import (from C#), NOT a <script>
        // tag in App.razor. A <script src=".../drawflow.min.js"> would force the ~46KB lib onto every
        // non-editor page. Regression-lock that App.razor stays clean.
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();

        html.Should().NotContain("drawflow.min.js",
            "Drawflow MUST be imported lazily by dag-editor.js, NOT via a <script> tag in App.razor");
        html.Should().NotContain("dag-editor.js",
            "dag-editor.js MUST be imported dynamically from DrawflowCanvas, NOT via a <script> tag");
    }
}
