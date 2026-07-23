using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.AspNetCore;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Per-circuit <see cref="IUKBatchClientFactory"/> used under per-user authentication. Each circuit
/// (one signed-in user) gets its own <see cref="RestUKBatchClient"/> per service, so the hub socket and
/// the forwarded bearer carry that user's identity rather than a single shared machine identity.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to the circuit. The clients connect lazily on first use (there is no startup conductor under
/// per-user authentication — at boot no user is present, so an eager connect to a gated hub would loop
/// on 401). Clients are disposed on circuit teardown.
/// </para>
/// <para>
/// The REST <see cref="HttpClient"/> wraps the pooled named-client handler chain
/// (<c>UKBatch.Dashboard.{service}</c>) with a <see cref="UKBatchTokenForwardingHandler"/> bound to this
/// scope's token accessor. Binding the accessor to the handler directly (rather than adding it to the
/// factory's pooled handler chain) keeps the token tied to the current user's scope: a pooled handler
/// resolves its dependencies from a handler-lifetime scope that has no circuit principal and would
/// forward nothing.
/// </para>
/// </remarks>
internal sealed class PerCircuitUKBatchClientProvider : IUKBatchClientFactory, IAsyncDisposable
{
    private readonly IUKBatchServiceRegistry _registry;
    private readonly IHttpMessageHandlerFactory _handlerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<DashboardOptions> _options;
    private readonly IUKBatchUserTokenAccessor _tokenAccessor;
    private readonly ConcurrentDictionary<string, RestUKBatchClient> _clients = new(StringComparer.Ordinal);
    private int _disposed; // 0 = live, 1 = disposed

    public PerCircuitUKBatchClientProvider(
        IUKBatchServiceRegistry registry,
        IHttpMessageHandlerFactory handlerFactory,
        ILoggerFactory loggerFactory,
        IOptions<DashboardOptions> options,
        IUKBatchUserTokenAccessor tokenAccessor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenAccessor);

        _registry = registry;
        _handlerFactory = handlerFactory;
        _loggerFactory = loggerFactory;
        _options = options;
        _tokenAccessor = tokenAccessor;
    }

    public IUKBatchClient GetClient(string serviceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        return _clients.GetOrAdd(serviceName, name =>
        {
            var descriptor = _registry.TryGet(name)
                ?? throw new UKBatchServiceNotRegisteredException(name);

            // Reuse the pooled named-client handler chain (rotation + primary handler are factory-owned);
            // wrap it with a forwarding handler bound to THIS scope's accessor. disposeHandler:false so
            // disposing the client's HttpClient never disposes the shared pooled inner handler.
            var forwarding = new UKBatchTokenForwardingHandler(
                _tokenAccessor,
                _loggerFactory.CreateLogger<UKBatchTokenForwardingHandler>())
            {
                InnerHandler = _handlerFactory.CreateHandler($"UKBatch.Dashboard.{name}"),
            };
            var http = new HttpClient(forwarding, disposeHandler: false)
            {
                BaseAddress = descriptor.BaseUrl,
                Timeout = _options.Value.HttpTimeout,
            };
            if (!string.IsNullOrEmpty(descriptor.ApiKey))
            {
                http.DefaultRequestHeaders.Add("X-Api-Key", descriptor.ApiKey);
            }

            // The static descriptor.Headers (machine-identity bearer / dev headers) are deliberately not
            // applied under per-user authentication — the forwarding handler supplies the user's bearer.
            return new RestUKBatchClient(
                descriptor,
                http,
                _loggerFactory.CreateLogger<RestUKBatchClient>(),
                _options,
                _tokenAccessor,
                lazyConnect: true);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var client in _clients.Values)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Logged by the client itself; teardown must not throw.
            }
        }

        _clients.Clear();
    }
}
