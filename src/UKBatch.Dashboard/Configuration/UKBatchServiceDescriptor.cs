namespace UKBatch.Dashboard.Configuration;

/// <summary>
/// Per-service configuration: name, base URL, hub path, optional API key, display name, tags.
/// Bound from <c>UKBatch:Dashboard:Services[]</c> in <c>appsettings.json</c> at host startup.
/// </summary>
/// <remarks>
/// <para><b>Name contract:</b> kebab-case <c>^[a-z][a-z0-9-]*$</c>. Used as the URL path segment
/// (<c>/dashboard/{name}/jobs</c>) AND as the cache key in <see cref="Clients.IUKBatchClientFactory"/>.
/// Validated by <see cref="DashboardOptionsValidator"/>.</para>
/// <para><b>BaseUrl contract:</b> absolute URI ending in <c>/api</c> (or the route group the
/// caller used at <c>MapUKBatchApi</c> time). A missing trailing slash is auto-appended on
/// assignment, so <c>http://service.local:5000/api</c> and <c>http://service.local:5000/api/</c>
/// behave identically. The hub URL is constructed as <c>BaseUrl + HubPath</c> — e.g.
/// <c>http://service.local:5000/api/</c> + <c>/hubs/jobs</c>
/// → <c>http://service.local:5000/api/hubs/jobs</c>.</para>
/// <para><b>ApiKey reserved:</b> v0.1 does NOT consume the field at the REST or hub layer (auth is
/// caller-opt-in via <c>RequireAuthorization</c>). The field exists for v0.2 cross-service auth
/// scenarios. Documented in the field xmldoc.</para>
/// </remarks>
public sealed record class UKBatchServiceDescriptor
{
    /// <summary>Kebab-case service slug — URL path segment + cache key.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Absolute REST base URL — e.g. <c>http://service.local:5000/api</c>. Auto-normalized to end
    /// with a trailing slash on assignment. <see cref="System.Net.Http.HttpClient.BaseAddress"/>
    /// per RFC 3986 drops the last path segment when joining a relative URI, so a base of
    /// <c>http://service.local:5000/api</c> would resolve <c>"jobs"</c> to <c>.../jobs</c> (losing
    /// the <c>/api</c> segment). Appending the slash makes the base safe to combine with the bare
    /// relative paths used by the REST client. A relative URI is passed through unchanged so the
    /// options validator can report the "must be an absolute URI" error.
    /// </summary>
    public required Uri BaseUrl
    {
        get => _baseUrl;
        init => _baseUrl = Normalize(value);
    }

    private readonly Uri _baseUrl = null!;

    private static Uri Normalize(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Relative URIs cannot expose AbsoluteUri; leave them for the validator to reject.
        if (!value.IsAbsoluteUri)
        {
            return value;
        }
        var absolute = value.AbsoluteUri;
        return absolute.EndsWith('/') ? value : new Uri(absolute + "/");
    }

    /// <summary>SignalR hub path relative to <see cref="BaseUrl"/>. Default <c>/hubs/jobs</c>.</summary>
    public string HubPath { get; init; } = "/hubs/jobs";

    /// <summary>
    /// Optional API key forwarded as <c>X-Api-Key</c> header on every REST + hub request.
    /// <b>v0.1 reserved</b> — not consumed server-side yet; v0.2 cross-service auth seam.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Static headers applied to every REST + hub request for this service — bearer / API-key /
    /// dev-auth. The general form of the reserved <see cref="ApiKey"/> (which is the <c>X-Api-Key</c>
    /// special case). Forwarded verbatim on each named <see cref="HttpClient"/> and the SignalR
    /// connection. <c>null</c> (the default) applies nothing.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Human-friendly display name (sidebar, breadcrumb). Defaults to <see cref="Name"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>User-supplied tags for grouping in multi-service views (e.g. <c>["prod","eu-west"]</c>).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
