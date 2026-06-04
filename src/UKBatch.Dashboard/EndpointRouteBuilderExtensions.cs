using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using UKBatch.Dashboard.Components;

namespace UKBatch.Dashboard;

/// <summary>Routes the Blazor Server dashboard at literal <c>/dashboard/...</c> paths.</summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Razor Components App at the literal <c>/dashboard/...</c> routes. Returns the
    /// builder so callers can chain auth attribute conventions
    /// (e.g. <c>.RequireAuthorization()</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Routes are PINNED LITERAL</b> — no <c>BasePath</c> option.
    /// All pages + the visual-editor placeholder use <c>@page "/dashboard/..."</c> directives.</para>
    /// <para>Returns <see cref="RazorComponentsEndpointConventionBuilder"/>
    /// so callers can chain <c>.RequireAuthorization(...)</c>.</para>
    /// <para><b>Caller MUST also call <c>app.UseAntiforgery()</c></b>
    /// in the request pipeline. Razor Components emit anti-forgery metadata; without
    /// <c>UseAntiforgery()</c> middleware, requests return 500 with the message
    /// "Endpoint /dashboard contains anti-forgery metadata, but a middleware was not found
    /// that supports anti-forgery." Place <c>UseAntiforgery()</c> after <c>UseRouting()</c> /
    /// <c>UseAuthorization()</c> in the standard ASP.NET Core pipeline order.</para>
    /// <para><b>Service registry <c>BaseUrl</c> MUST end with a
    /// trailing slash.</b> <see cref="System.Net.Http.HttpClient.BaseAddress"/> per RFC 3986
    /// strips the last segment when joining a relative URI; the typed-HttpClient REST calls
    /// inside <c>RestUKBatchClient</c> use bare relative paths (e.g. <c>"jobs"</c>) so the
    /// final URI for a base of <c>http://localhost:5000/api/</c> is <c>.../api/jobs</c>, while
    /// a base of <c>http://localhost:5000/api</c> drops the <c>api</c> segment and resolves to
    /// <c>http://localhost:5000/jobs</c> (404 against any UKBatch.Api mount). The validator
    /// does NOT enforce this in v0.1; v0.2 may auto-append.</para>
    /// <para><b>HOST PROJECT MUST set
    /// <c>&lt;RequiresAspNetWebAssets&gt;true&lt;/RequiresAspNetWebAssets&gt;</c> in its csproj
    /// PropertyGroup,</b> OR install <c>UKBatch.Dashboard</c> via PackageReference (the NuGet
    /// package ships <c>build/UKBatch.Dashboard.props</c> which auto-applies the prop). Without
    /// it, the Web SDK's auto-detection (Microsoft.NET.Sdk.Web.ProjectSystem.targets:32) does NOT
    /// add the Razor Components framework assets (notably <c>_framework/blazor.web.js</c> and
    /// <c>_framework/blazor.server.js</c>) to the static-web-assets manifest because no
    /// <c>.razor</c> files exist in the host project — they all live inside
    /// <c>UKBatch.Dashboard</c>. The dashboard page then renders as static HTML but button
    /// clicks and SignalR live updates silently fail with a network 404 the operator only
    /// discovers from browser DevTools. ProjectReference does NOT propagate the NuGet
    /// <c>build/*.props</c> file, so solution-internal hosts (samples, integration tests) must
    /// set the prop manually.</para>
    /// </remarks>
    public static RazorComponentsEndpointConventionBuilder MapUKBatchDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }
}
