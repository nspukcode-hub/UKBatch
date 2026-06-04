namespace UKBatch.Transport.Http;

/// <summary>
/// Pluggable service discovery seam. v0.1 ships a static configuration-bound resolver only (via
/// <see cref="HttpTransportOptions.Services"/>). v0.2.0+ adapter packages
/// (<c>UKBatch.Transport.Http.Consul</c>, <c>.Eureka</c>, <c>.Dns</c>) implement this interface to
/// swap dynamic resolution behind the same call site.
/// </summary>
/// <remarks>
/// <para><b>v0.1 contract:</b> NOT registered by <see cref="ServiceCollectionExtensions.AddUKBatchHttpTransport(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{HttpTransportOptions}?)"/>.
/// Resolving <see cref="IServiceDiscovery"/> from DI returns <c>null</c>. <see cref="HttpTransport"/>
/// bypasses this seam when null and reads <see cref="HttpTransportOptions.Services"/> directly.</para>
/// <para><b>v0.2 contract:</b> concrete adapter registers itself with <c>TryAddSingleton</c>.
/// <see cref="HttpTransport"/> resolves the interface and prefers it; falls back to the static dict
/// if the resolver returns <c>null</c> for a given service.</para>
/// <para><b>Thread-safety:</b> implementations MUST be thread-safe (one singleton serves all
/// concurrent <c>ITransport</c> callers).</para>
/// </remarks>
public interface IServiceDiscovery
{
    /// <summary>
    /// Resolves the endpoint for <paramref name="serviceName"/>; returns <c>null</c> if unknown.
    /// </summary>
    /// <param name="serviceName">Logical service name (matches <c>JobMessage.TargetService</c>).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    Task<ServiceEndpoint?> ResolveAsync(string serviceName, CancellationToken cancellationToken);
}
