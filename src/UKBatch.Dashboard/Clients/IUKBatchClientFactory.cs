namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Resolves <see cref="IUKBatchClient"/> by service name. Singleton-scoped; internal
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> caches one
/// client per service across the entire host.
/// </summary>
/// <remarks>
/// <para><b>Lookup is throwing on miss:</b> calling <see cref="GetClient"/> with a name not in
/// the registry throws <see cref="UKBatchServiceNotRegisteredException"/>. Page components
/// resolve client via <c>IUKBatchServiceRegistry.TryGet</c> first, then call <c>GetClient</c>
/// on a verified-existing name.</para>
/// <para><b>Connection lifecycle:</b> the factory does NOT call <c>ConnectAsync</c>;
/// <c>UKBatchServiceConductor</c> (<c>IHostedService</c>) does that on host startup.</para>
/// </remarks>
public interface IUKBatchClientFactory
{
    /// <summary>Returns the singleton client for the named service. Throws if the name is not registered.</summary>
    IUKBatchClient GetClient(string serviceName);
}
