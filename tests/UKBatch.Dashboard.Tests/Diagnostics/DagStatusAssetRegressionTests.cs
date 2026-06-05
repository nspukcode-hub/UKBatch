using System.Net;
using FluentAssertions;
using UKBatch.Dashboard.Tests.Integration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

/// <summary>
/// HttpClient asset regression for the read-only status canvas ES
/// module (<c>_content/UKBatch.Dashboard/js/dag-status.js</c>). Mirrors
/// <see cref="DagCanvasAssetRegressionTests"/>: bunit's mocked JSInterop CANNOT catch a runtime
/// module 404 — exactly the silent-failure mode hit with <c>blazor.web.js</c>.
/// When the asset 404s, <c>DagStatusCanvas</c> degrades to its fallback list with no clear signal, so
/// only an HttpClient asset test locks the contract.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DagStatusAssetRegressionTests : IClassFixture<SampleDashboardFactory>
{
    private readonly SampleDashboardFactory _factory;

    public DagStatusAssetRegressionTests(SampleDashboardFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DagStatusJs_IsServedAt_StaticWebAssetsPath()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-status.js", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the status-canvas ES module MUST be in the static-web-assets manifest of the host project — " +
            "absence indicates the build/UKBatch.Dashboard.props recipe broke or the file got renamed");
    }

    [Fact]
    public async Task DagStatusJs_BodyContainsExpectedEsModuleSurface()
    {
        // DagStatusCanvas imports the module and invokes `init` + `dispose`, and the module is read-only
        // ('fixed' mode). If the `init` export or the read-only marker disappear, OnAfterRenderAsync
        // silently degrades (graceful fallback) with no production signal. This test pins the contract.
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-status.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Length.Should().BeGreaterThan(500, "the module is non-trivial — a near-empty body indicates a broken build");
        body.Should().Contain("export async function init",
            "DagStatusCanvas calls module.InvokeAsync<IJSObjectReference>(\"init\",...) — the export must exist");
        body.Should().Contain("export function dispose",
            "DagStatusCanvas calls module.InvokeVoidAsync(\"dispose\",...) — the export must exist");
        body.Should().Contain("editor_mode",
            "the read-only canvas sets editor_mode='fixed' — its absence means mutation may have leaked in");
    }

    [Fact]
    public async Task DagStatusJs_ServedBody_EncodesQuotesInEscapeHtml()
    {
        // escapeHtml output is interpolated into QUOTED attribute contexts (e.g. title="..."). The
        // DOM-textContent trick alone encodes < > & but NOT " or ', so a step/service name like
        // a" onmouseover="..." would break out of the attribute and inject a live event handler. The
        // served asset MUST carry the quote-encoding replaces. bunit's mocked JSInterop never loads the
        // real module, so only an HttpClient body assertion locks this — a future simplification that
        // drops the replaces fails CI here.
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-status.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("&quot;",
            "escapeHtml MUST encode the double quote so values stay inside quoted attributes — " +
            "its absence reopens the attribute-context injection seam");
        body.Should().Contain("&#39;",
            "escapeHtml MUST encode the single quote so values stay inside single-quoted attributes");
    }

    [Fact]
    public async Task DagStatusJs_ContentTypeIsJavaScript()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/js/dag-status.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var ct = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        ct.Should().NotBeEmpty();
        ct.Contains("javascript", StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            $"dag-status.js MUST be served with a JavaScript MIME type (got '{ct}') — ESM imports refuse text/html / octet-stream");
    }

    [Fact]
    public async Task DashboardLanding_DoesNotEmitDagStatusScriptTag()
    {
        // The module is loaded via dynamic `import` from C#, NOT a <script> tag in App.razor. A
        // <script src=".../dag-status.js"> tag would mis-apply the ES module as a classic script.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();

        html.Should().NotContain("dag-status.js",
            "dag-status.js MUST be imported dynamically from DagStatusCanvas, NOT via a <script> tag in App.razor");
    }

    [Fact]
    public async Task VendoredDrawflow_StillServed()
    {
        // The read-only canvas classic-script-loads the vendored UMD Drawflow at runtime. Its 404 means
        // init throws → permanent fallback list in production.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            new Uri("/_content/UKBatch.Dashboard/lib/drawflow/drawflow.min.js", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the vendored Drawflow UMD bundle MUST be served — dag-status.js classic-script-loads it at init");
    }
}
