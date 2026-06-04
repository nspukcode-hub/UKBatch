using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Default <see cref="IUKBatchClientFactory"/>. App-scoped <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// caches one <see cref="RestUKBatchClient"/> per service across the entire host.
/// </summary>
internal sealed class UKBatchClientFactory : IUKBatchClientFactory, IAsyncDisposable
{
    private readonly IUKBatchServiceRegistry _registry;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<DashboardOptions> _options;
    private readonly ConcurrentDictionary<string, RestUKBatchClient> _clients = new(StringComparer.Ordinal);
    private int _disposed; // 0 = live, 1 = disposed

    public UKBatchClientFactory(
        IUKBatchServiceRegistry registry,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        IOptions<DashboardOptions> options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
        _options = options;
    }

    public IUKBatchClient GetClient(string serviceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        return _clients.GetOrAdd(serviceName, name =>
        {
            var descriptor = _registry.TryGet(name)
                ?? throw new UKBatchServiceNotRegisteredException(name);
            // Named HttpClient — per-service descriptor configured via HttpClientFactoryNamedConfigurator
            // (BaseAddress + Timeout + X-Api-Key header). The configurator runs lazily on first
            // CreateClient call, so descriptor changes after host start would NOT propagate (hot-reload deferred to v0.2).
            var http = _httpFactory.CreateClient($"UKBatch.Dashboard.{name}");
            return new RestUKBatchClient(descriptor, http, _loggerFactory.CreateLogger<RestUKBatchClient>(), _options);
        });
    }

    /// <summary>Used by <see cref="UKBatchServiceConductor"/> for bulk enumeration on startup/shutdown.</summary>
    internal IEnumerable<RestUKBatchClient> SnapshotClients() => _clients.Values.ToArray();

    /// <summary>
    /// Disposes every cached <see cref="RestUKBatchClient"/>. The underlying
    /// <see cref="HttpClient"/> instances are owned by <see cref="IHttpClientFactory"/>
    /// (ASP.NET Core registers it as a singleton) and are NOT disposed here. Only the hub
    /// connection + sync primitives held by each <see cref="RestUKBatchClient"/> are released.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Logged by client itself.
            }
        }
        _clients.Clear();
    }
}
