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
    /// <para><b>Service registry <c>BaseUrl</c> trailing slash:</b>
    /// <see cref="System.Net.Http.HttpClient.BaseAddress"/> per RFC 3986 strips the last segment
    /// when joining a relative URI; the typed-HttpClient REST calls inside <c>RestUKBatchClient</c>
    /// use bare relative paths (e.g. <c>"jobs"</c>) so the base must end with a trailing slash for
    /// the final URI to be <c>.../api/jobs</c> rather than <c>.../jobs</c> (which would drop the
    /// <c>api</c> segment and 404 against any UKBatch.Api mount).
    /// <see cref="Configuration.UKBatchServiceDescriptor.BaseUrl"/> now auto-appends the missing
    /// trailing slash, so a configured base of <c>http://localhost:5000/api</c> works the same as
    /// <c>http://localhost:5000/api/</c> — no operator action required.</para>
    /// <para><b>On .NET 10, the host project MUST set
    /// <c>&lt;RequiresAspNetWebAssets&gt;true&lt;/RequiresAspNetWebAssets&gt;</c> in its own csproj
    /// PropertyGroup</b> — for BOTH ProjectReference and PackageReference. The Web SDK only adds the
    /// Razor Components framework assets (notably <c>_framework/blazor.web.js</c> and
    /// <c>_framework/blazor.server.js</c>) to the static-web-assets manifest when it detects
    /// <c>.razor</c> files in the host (Microsoft.NET.Sdk.Web.ProjectSystem.targets:32); here they
    /// all live inside <c>UKBatch.Dashboard</c>, so the host needs the explicit prop. NuGet cannot
    /// supply it automatically — the property is read during restore, before a package's build
    /// assets are imported. Without it the dashboard renders as static HTML and button clicks and
    /// SignalR live updates fail with a network 404 only browser DevTools reveals. The package ships
    /// a build target that raises warning <c>UKBATCH001</c> on .NET 10 when the prop is missing,
    /// turning that silent runtime 404 into a build-time message. (.NET 8 hosts do not need it —
    /// <c>MapRazorComponents</c> serves the framework assets there.)</para>
    /// </remarks>
    public static RazorComponentsEndpointConventionBuilder MapUKBatchDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }
}
