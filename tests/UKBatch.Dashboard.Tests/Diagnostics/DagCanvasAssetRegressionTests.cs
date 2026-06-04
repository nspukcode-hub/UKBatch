using System.Net;
using FluentAssertions;
using UKBatch.Dashboard.Tests.Integration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// Locks down the <c>dag-canvas.js</c> ES module asset path
/// (<c>_content/UKBatch.Dashboard/js/dag-canvas.js</c>). bunit's mocked JSInterop CANNOT catch a
/// runtime module 404 — exactly the silent-failure mode hit with
/// <c>blazor.web.js</c>. Only an HttpClient asset test does.
/// </summary>
/// <remarks>
/// Also folds in: the <c>/_blazor/negotiate</c> SignalR transport-negotiate endpoint must
/// remain reachable so Interactive Server hub connections can establish.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DagCanvasAssetRegressionTests : IClassFixture<SampleDashboardFactory>
{
    private readonly SampleDashboardFactory _factory;

    public DagCanvasAssetRegressionTests(SampleDashboardFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DagCanvasJs_IsServedAt_StaticWebAssetsPath()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-canvas.js", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the DAG canvas ES module MUST be in the static-web-assets manifest of the host project — " +
            "absence indicates the build/UKBatch.Dashboard.props recipe broke or the file got renamed");
    }

    [Fact]
    public async Task DagCanvasJs_BodyContainsExpectedEsModuleExports()
    {
        // The DagView component imports the module and invokes `init` + `dispose`. If either export
        // disappears, OnAfterRenderAsync silently catches the JSException (graceful degradation) and
        // wheel/drag goes dead in production with no clear signal. This test pins the contract.
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-canvas.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("export function init",
            "DagView.razor calls module.InvokeAsync<IJSObjectReference>(\"init\",...) — the export must exist");
        body.Should().Contain("export function dispose",
            "DagView.razor calls module.InvokeVoidAsync(\"dispose\", canvas) — the export must exist");
    }

    [Fact]
    public async Task DagCanvasJs_ContentTypeIsJavaScript()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-canvas.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var ct = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        // Static-web-assets may emit `text/javascript` or `application/javascript` — both are
        // legitimate; we just guard against `text/html` (route fall-through) or `application/octet-stream`
        // (mime missing — browser refuses to import as ESM).
        ct.Should().NotBeEmpty();
        (ct.Contains("javascript", StringComparison.OrdinalIgnoreCase)).Should().BeTrue(
            $"dag-canvas.js MUST be served with a JavaScript MIME type (got '{ct}') — " +
            "ESM imports refuse text/html / octet-stream");
    }

    [Fact]
    public async Task DashboardLanding_DoesNotEmitDagCanvasScriptTag()
    {
        // The module is loaded via dynamic `import` from C#, NOT a <script> tag in App.razor.
        // If anyone adds a <script src=".../dag-canvas.js"> tag, the global IIFE pattern would be
        // mis-applied (the file is an ES module). Regression-lock that App.razor stays untouched
        // wrt this module.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();

        html.Should().NotContain("dag-canvas.js",
            "dag-canvas.js MUST be imported dynamically from DagView, NOT via a <script> tag in App.razor");
    }

    [Fact]
    public async Task DashboardCss_StillServed()
    {
        // Regression guard against the FF-1 lesson: if static-web-assets break, CSS goes too.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/css/dashboard.css", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "dashboard.css is the design-system stylesheet — its 404 means the static-web-assets manifest is broken");
    }

    // ── /_blazor/negotiate transport-negotiate regression lock ───────────

    [Fact]
    public async Task BlazorSignalRNegotiate_IsReachable()
    {
        // The SignalR transport-negotiate endpoint /_blazor/negotiate is registered by
        // AddInteractiveServerRenderMode + MapRazorComponents<App>. If the host pipeline regresses,
        // hub connections silently fail in production — exactly the latent
        // class of bug. The endpoint accepts POST with negotiateVersion query.
        using var client = _factory.CreateClient();
        using var content = new StringContent(string.Empty);
        var resp = await client.PostAsync(
            new Uri("/_blazor/negotiate?negotiateVersion=1", UriKind.Relative),
            content);

        // 200/204 are both legitimate negotiate responses; 404 indicates the wiring is gone.
        resp.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "/_blazor/negotiate MUST exist for Interactive Server transport — 404 indicates the host pipeline regressed");
        // 2 prereq: strengthen the assertion so a 403 cannot pass green (the old test only
        // guarded 404, so an antiforgery-gated negotiate would have slipped through as a false pass).
        // NB the actual manual-smoke "403" was macOS AirPlay/Control Center holding
        // port 5000 (Server: AirTunes/*), NOT antiforgery — the Sample.Dashboard now binds:5057.
        // This assertion guards against a genuine future antiforgery-on-hub misconfiguration.
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "/_blazor/negotiate MUST NOT be antiforgery-gated — a 403 here would block Interactive Server circuit init");
    }
}
