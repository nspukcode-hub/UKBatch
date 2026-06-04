using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using UKBatch.Transport.Http.Auth;

namespace UKBatch.Transport.Http.Resilience;

/// <summary>
/// Polly v8 pipeline composition for the named HTTP transport client. UKBatch
/// ordering: <c>Timeout → CircuitBreaker → Retry</c> (vs Microsoft default
/// <c>Total Timeout → Retry → CircuitBreaker → Attempt Timeout</c>).
/// </summary>
/// <remarks>
/// <para><b>Why CB-outside-Retry:</b> circuit breaker observes outcomes BEFORE retry decides to
/// re-attempt; under sustained transient failure, CB opens after threshold and subsequent retries
/// fail-fast (no socket/connection overhead). Appropriate for cross-service IPC where retry storms
/// against a degraded peer worsen the outage cascade.</para>
/// <para><b>4xx does NOT retry:</b> only <see cref="HttpRequestException"/> + 5xx + 408 + 503. 4xx
/// is a caller error (bad signature, unknown job) — retrying wastes the budget.</para>
/// </remarks>
internal static class PollyResilienceHandlerSetup
{
    /// <summary>Default per-service named-client identifier prefix.</summary>
    public const string NamedClientPrefix = "ukbatch-http-transport";

    /// <summary>
    /// Registers the named-client + resilience pipeline. The <see cref="HttpTransport"/> resolves a
    /// per-service client via <c>factory.CreateClient($"{NamedClientPrefix}:{serviceName}")</c>.
    /// </summary>
    /// <remarks>
    /// v0.1: a single shared pipeline is registered against the bare prefix (all services share the
    /// same retry/CB/timeout budget). Per-service tuning is a v0.2 hook (operators register a custom
    /// <c>IServiceCollection.AddHttpClient($"{NamedClientPrefix}:{serviceName}").AddResilienceHandler(...)</c>
    /// AFTER <c>AddUKBatchHttpTransport</c> to override).
    /// </remarks>
    public static void RegisterNamedClients(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // HmacSigningHandler registered as transient — DelegatingHandler instances
        // MUST be transient when used via AddHttpMessageHandler<T> (HttpClientFactory creates a fresh
        // handler per HttpClient instantiation; reusing a singleton DelegatingHandler causes ObjectDisposedException
        // when InnerHandler chain rotates).
        services.AddTransient<HmacSigningHandler>();

        var httpClientBuilder = services.AddHttpClient(NamedClientPrefix)
            .ConfigureHttpClient((sp, client) =>
            {
                // HttpClient.Timeout INFINITE — Polly is the authoritative timeout.
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        httpClientBuilder.AddResilienceHandler("ukbatch-pipeline", (builder, ctx) =>
            {
                var options = ctx.ServiceProvider.GetRequiredService<IOptions<HttpTransportOptions>>().Value;
                var logger = ctx.ServiceProvider.GetRequiredService<ILogger<HttpTransport>>();

                // Outer timeout — wall-clock budget across retries (caller CT remains authoritative).
                builder.AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = options.DefaultRequestTimeout,
                    Name = "ukbatch-outer-timeout",
                });

                // Circuit breaker.
                builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    Name = "ukbatch-cb",
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(static r =>
                            (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.RequestTimeout),
                    FailureRatio = 1.0,
                    MinimumThroughput = options.CircuitBreakerThreshold,
                    SamplingDuration = options.CircuitBreakerWindow,
                    BreakDuration = options.CircuitBreakerWindow,
                });

                // Retry — exponential backoff + jitter from configured delays.
                var delays = options.RetryDelays?.ToArray() ?? new[]
                {
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(15),
                };
                builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    Name = "ukbatch-retry",
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(static r =>
                            (int)r.StatusCode >= 500
                            || r.StatusCode == HttpStatusCode.RequestTimeout
                            || r.StatusCode == HttpStatusCode.ServiceUnavailable),
                    MaxRetryAttempts = delays.Length,
                    DelayGenerator = args =>
                    {
                        var idx = Math.Min(args.AttemptNumber, delays.Length - 1);
                        var d = delays[idx];
                        var jitter = TimeSpan.FromMilliseconds(d.TotalMilliseconds * 0.1 * Random.Shared.NextDouble());
                        return ValueTask.FromResult<TimeSpan?>(d + jitter);
                    },
                    OnRetry = args =>
                    {
                        logger.LogWarning(args.Outcome.Exception,
                            "HTTP retry attempt {Attempt} after {Delay}ms.",
                            args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                        return ValueTask.CompletedTask;
                    },
                });

                // Inner per-attempt timeout (v0.1 same as outer).
                builder.AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = options.DefaultRequestTimeout,
                    Name = "ukbatch-inner-timeout",
                });
            });

        // HmacSigningHandler chained AFTER resilience so Polly retries
        // re-invoke the handler → fresh nonce + timestamp per attempt (the nonce ROTATEs per
        // retry). AddHttpMessageHandler appends to the
        // inner handler chain — added AFTER AddResilienceHandler, so it executes INSIDE the
        // retry loop (each retry attempt re-invokes HmacSigningHandler.SendAsync).
        httpClientBuilder.AddHttpMessageHandler<HmacSigningHandler>();
    }
}
