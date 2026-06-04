using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.State;

/// <summary>
/// Per-circuit (Blazor Server scoped) state. Holds the currently-active service drill-down +
/// theme preference. One instance per circuit; disposed on circuit termination.
/// </summary>
/// <remarks>
/// <para><b>Lifetime:</b> registered as <c>Scoped</c> by <c>AddUKBatchDashboard</c>.</para>
/// <para><b>Holds NO live data.</b> Page components own per-page data, subscribe to
/// <c>IUKBatchClient</c> events directly, and dispose on navigation.</para>
/// </remarks>
public interface IDashboardState
{
    /// <summary>Currently-selected service. Set by page components on entry; null = no selection (Landing / Settings).</summary>
    UKBatchServiceDescriptor? CurrentService { get; set; }

    /// <summary>Theme preference — defaults to <see cref="DashboardTheme.Dark"/>.</summary>
    DashboardTheme Theme { get; set; }

    /// <summary>
    /// Raised after <see cref="CurrentService"/> changes to a different service (by reference or
    /// <see cref="UKBatchServiceDescriptor.Name"/>). Lets shell components (breadcrumb / sidebar)
    /// react without polling. Subscribers MUST hop to the render dispatcher
    /// (<c>await InvokeAsync(StateHasChanged)</c>) and MUST unsubscribe on dispose.
    /// </summary>
    event Action<UKBatchServiceDescriptor?>? CurrentServiceChanged;
}

/// <summary>Theme variants supported by the dashboard. Theme tokens live in <c>wwwroot/css/dashboard.css</c>.</summary>
public enum DashboardTheme
{
    /// <summary>Default dark theme — high contrast for operator workstations.</summary>
    Dark = 0,
    /// <summary>Light theme override. The design system is dark-first; light is passable v0.1, full a11y audit deferred to v0.2.</summary>
    Light = 1,
}
