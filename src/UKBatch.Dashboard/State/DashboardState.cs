using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.State;

/// <summary>Default implementation of <see cref="IDashboardState"/> — scoped (per-circuit).</summary>
internal sealed class DashboardState : IDashboardState
{
    private UKBatchServiceDescriptor? _currentService;

    public UKBatchServiceDescriptor? CurrentService
    {
        get => _currentService;
        set
        {
            // Raise only on an actual change (reference OR Name inequality) so re-setting the
            // same service from a page's OnInitialized does not spam subscribers.
            if (ReferenceEquals(_currentService, value)) return;
            if (_currentService is not null && value is not null &&
                string.Equals(_currentService.Name, value.Name, StringComparison.Ordinal))
            {
                _currentService = value;
                return;
            }
            _currentService = value;
            CurrentServiceChanged?.Invoke(value);
        }
    }

    public DashboardTheme Theme { get; set; } = DashboardTheme.Dark;

    public event Action<UKBatchServiceDescriptor?>? CurrentServiceChanged;
}
