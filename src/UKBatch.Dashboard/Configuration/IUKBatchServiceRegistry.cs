namespace UKBatch.Dashboard.Configuration;

/// <summary>
/// Read-only registry of configured UKBatch services. Default implementation:
/// <c>StaticServiceRegistry</c> (appsettings-bound). v0.2 will add Consul / Eureka adapter
/// NuGets that implement this interface for dynamic service discovery.
/// </summary>
/// <remarks>
/// <para><b>Registration order:</b> <see cref="All"/> returns descriptors in the order they
/// appear in <c>DashboardOptions.Services</c>. Sidebar nav renders in this order.</para>
/// <para><b>Name uniqueness:</b> enforced by <see cref="DashboardOptionsValidator"/>; the registry
/// itself does NOT defend against duplicates (it trusts the validator).</para>
/// </remarks>
public interface IUKBatchServiceRegistry
{
    /// <summary>All registered descriptors in registration order. Returns a snapshot (defensive copy NOT required by contract — callers MUST NOT mutate).</summary>
    IReadOnlyList<UKBatchServiceDescriptor> All();

    /// <summary>Returns the descriptor for the given name, or <c>null</c> if no service is registered with that name.</summary>
    UKBatchServiceDescriptor? TryGet(string name);
}
