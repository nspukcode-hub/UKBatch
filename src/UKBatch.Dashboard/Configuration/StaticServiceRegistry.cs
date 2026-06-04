using Microsoft.Extensions.Options;

namespace UKBatch.Dashboard.Configuration;

/// <summary>
/// Default <see cref="IUKBatchServiceRegistry"/> implementation. Captures the
/// <see cref="DashboardOptions.Services"/> snapshot at construction; subsequent
/// <c>IOptionsMonitor</c> updates do NOT propagate (hot-reload deferred to v0.2).
/// </summary>
internal sealed class StaticServiceRegistry : IUKBatchServiceRegistry
{
    private readonly IReadOnlyList<UKBatchServiceDescriptor> _all;
    private readonly Dictionary<string, UKBatchServiceDescriptor> _byName;

    public StaticServiceRegistry(IOptions<DashboardOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Defensive snapshot at construction — DashboardOptions.Services is a mutable List<T>
        // bound from appsettings; we capture once at startup. Subsequent appsettings reloads do
        // NOT propagate (hot-reload deferred to v0.2).
        _all = options.Value.Services.ToArray();
        _byName = _all.ToDictionary(d => d.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<UKBatchServiceDescriptor> All() => _all;

    public UKBatchServiceDescriptor? TryGet(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _byName.TryGetValue(name, out var d) ? d : null;
    }
}
