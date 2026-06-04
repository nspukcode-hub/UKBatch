namespace UKBatch.Dashboard.Configuration;

/// <summary>
/// Configuration for <c>AddUKBatchDashboard</c>. Bound from <c>UKBatch:Dashboard</c> in
/// <c>appsettings.json</c>; overrideable via <c>configure</c> callback at registration.
/// </summary>
/// <remarks>
/// <para><b>Validation runs at host startup</b> via <see cref="DashboardOptionsValidator"/>
/// (registered as <c>IValidateOptions&lt;DashboardOptions&gt;</c>) — bad config fails fast,
/// before the host enters the request pipeline.</para>
/// <para><b>Property types follow ConfigurationBinder constraints:</b>
/// <see cref="Services"/> is <c>List&lt;T&gt;</c> (NOT <c>IReadOnlyList&lt;T&gt;</c>) so
/// appsettings binding populates it. Same applies to <see cref="ReconnectDelays"/>.</para>
/// <para><b>There is no <c>BasePath</c> option.</b> Routes are PINNED LITERAL
/// <c>/dashboard/...</c> in <c>@page</c> directives across all pages + the visual-editor
/// placeholder. A configurable BasePath would create a confusing dual source of truth (option value
/// vs hardcoded routes). v0.2 may revisit if mount-path
/// customisation has real demand.</para>
/// </remarks>
public sealed class DashboardOptions
{
    /// <summary>Registered UKBatch services. At least 1 is required. Order = sidebar render order.</summary>
    public List<UKBatchServiceDescriptor> Services { get; set; } = [];

    /// <summary>Default page size for paged lists (Jobs, Batches, Executions). Default <c>50</c>; must be &gt;= 1.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// Hub auto-reconnect delays. <c>null</c> ⇒ <c>RestUKBatchClient</c> generates jittered
    /// defaults <c>[2s+rand(0,1s), 5s+rand(0,2s), 10s+rand(0,3s), 30s+rand(0,5s)]</c>.
    /// Set explicit values to opt OUT of jitter (e.g. for deterministic tests).
    /// </summary>
    public List<TimeSpan>? ReconnectDelays { get; set; }

    /// <summary>LRU dedupe cache capacity (per cache type — exec / progress / batch-complete). Default <c>256</c>.</summary>
    public int DedupeCacheCapacity { get; set; } = 256;

    /// <summary>HTTP request timeout for REST calls. Default <c>30s</c>; must be &gt; 0.</summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Coalescing window for live-page UI refresh (<c>Batches/RunDetail</c>). A burst of hub events
    /// (up to 4× fan-out) is debounced into one <c>StateHasChanged</c> per window. Default <c>100ms</c>.
    /// </summary>
    public TimeSpan UiRefreshDebounce { get; set; } = TimeSpan.FromMilliseconds(100);
}
