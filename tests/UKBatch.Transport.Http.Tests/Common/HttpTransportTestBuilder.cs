using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Endpoints;
using UKBatch.Transport.Http.Receiver;
using UKBatch.Transport.Http.Resilience;

namespace UKBatch.Transport.Http.Tests.Common;

/// <summary>
/// Standalone builder for an in-test HttpTransport bound to the WAF worker.
/// Replaces the named-client's HTTP handler with the worker's TestServer.CreateHandler
/// — every outbound HTTP request from the test transport lands inside the worker's pipeline
/// in-process (hub stress pattern reuse for cross-service testing).
/// </summary>
public sealed class HttpTransportTestBuilder
{
    private readonly TestServer _workerServer;
    private string _sharedSecret = TestHmacHeaders.TestSecret;
    private TimeProvider _timeProvider = TimeProvider.System;
    private TimeSpan _defaultRequestTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _longPollMaxWait = TimeSpan.FromSeconds(5);
    private TimeSpan _circuitBreakerWindow = TimeSpan.FromSeconds(30);
    private int _circuitBreakerThreshold = 5;
    private TimeSpan[]? _retryDelays;
    private string _baseUrl = "http://billing-worker.test";
    private string _serviceName = "billing-worker";

    public HttpTransportTestBuilder(TestServer workerServer)
    {
        _workerServer = workerServer ?? throw new ArgumentNullException(nameof(workerServer));
    }

    public HttpTransportTestBuilder WithSecret(string secret)
    {
        _sharedSecret = secret;
        return this;
    }

    public HttpTransportTestBuilder WithTimeProvider(TimeProvider tp)
    {
        _timeProvider = tp;
        return this;
    }

    public HttpTransportTestBuilder WithRequestTimeout(TimeSpan t)
    {
        _defaultRequestTimeout = t;
        return this;
    }

    public HttpTransportTestBuilder WithRetryDelays(params TimeSpan[] delays)
    {
        _retryDelays = delays;
        return this;
    }

    public HttpTransportTestBuilder WithCircuitBreaker(int threshold, TimeSpan window)
    {
        _circuitBreakerThreshold = threshold;
        _circuitBreakerWindow = window;
        return this;
    }

    public HttpTransportTestBuilder WithService(string name, string baseUrl)
    {
        _serviceName = name;
        _baseUrl = baseUrl;
        return this;
    }

    /// <summary>
    /// Builds the transport against a real DI container. Returns the transport + the underlying
    /// <see cref="ServiceProvider"/> for cleanup. Caller MUST dispose the provider when done.
    /// </summary>
    public (global::UKBatch.Transport.Http.HttpTransport Transport, ServiceProvider Provider) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        services.AddSingleton(_timeProvider);
        services.Configure<HttpTransportOptions>(o =>
        {
            o.SharedSecret = _sharedSecret;
            o.DefaultRequestTimeout = _defaultRequestTimeout;
            o.LongPollMaxWait = _longPollMaxWait;
            o.CircuitBreakerThreshold = _circuitBreakerThreshold;
            o.CircuitBreakerWindow = _circuitBreakerWindow;
            if (_retryDelays is not null)
            {
                o.RetryDelays = _retryDelays;
            }
            // The test sentinel host is rewritten via TestServer handler injection — never a real
            // network hop — so opt into the non-loopback http endpoint explicitly.
            o.AllowInsecureHttp = true;
            o.Services.Add(_serviceName, new ServiceEndpoint { BaseUrl = new Uri(_baseUrl) });
        });
        // Validate options at the host start to surface config errors loudly.
        services.AddSingleton<IValidateOptions<HttpTransportOptions>, HttpTransportOptionsValidator>();
        services.AddSingleton<HmacSignatureService>();
        services.AddSingleton<NonceDedupeCache>(sp =>
            new NonceDedupeCache(sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.NonceCacheCapacity));
        services.AddSingleton<MessageIdDedupeCache>(sp =>
            new MessageIdDedupeCache(sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.MessageIdCacheCapacity));
        services.AddSingleton<HmacAuthorizationFilter>();
        services.AddSingleton<HttpTransportReceiver>();

        // Manually wire the resilience pipeline + HMAC signing handler to land at the worker's
        // TestServer.CreateHandler().
        PollyResilienceHandlerSetup.RegisterNamedClients(services);
        services.ConfigureHttpClientDefaults(b =>
        {
            // No-op; per-client overrides below.
        });
        // Replace the inner handler of the named transport client with the worker server's handler.
        services.AddHttpClient(PollyResilienceHandlerSetup.NamedClientPrefix)
            .ConfigurePrimaryHttpMessageHandler(() => _workerServer.CreateHandler());

        services.TryAddSingleton<global::UKBatch.Transport.Http.HttpTransport>(sp => new global::UKBatch.Transport.Http.HttpTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>(),
            sp.GetRequiredService<HttpTransportReceiver>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<global::UKBatch.Transport.Http.HttpTransport>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IServiceDiscovery>()));

        var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<global::UKBatch.Transport.Http.HttpTransport>();
        return (transport, provider);
    }
}
