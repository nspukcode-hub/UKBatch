using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
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
using Xunit;

namespace UKBatch.Transport.Http.Tests.Resilience;

/// <summary>
/// Polly v8 pipeline regression tests + lock (nonce rotates per attempt).
/// Uses a controllable stub <see cref="HttpMessageHandler"/> as the named-client's primary handler so
/// tests can deterministically inject 5xx / 4xx / HttpRequestException responses.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class PollyRetryTests
{
    private static JobMessage BuildMessage() => new JobMessage
    {
        MessageId = Guid.NewGuid().ToString("N"),
        CorrelationId = null,
        JobName = "test",
        SourceService = "src",
        TargetService = "service-x",
        BatchId = null,
        BatchStepId = null,
        Parameters = new Dictionary<string, object?>(),
        Headers = new Dictionary<string, string>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
    };

    private static (global::UKBatch.Transport.Http.HttpTransport Transport, ServiceProvider Sp, ScriptedHandler Handler) Build(
        ScriptedHandler handler,
        TimeSpan[]? retryDelays = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.Configure<HttpTransportOptions>(o =>
        {
            o.SharedSecret = "TEST-SECRET-FOR-VALIDATION-FLOOR-32CH+";
            // The stub endpoints below use non-loopback sentinel hosts (rewritten via handler injection).
            o.AllowInsecureHttp = true;
            o.DefaultRequestTimeout = TimeSpan.FromSeconds(30);
            o.LongPollMaxWait = TimeSpan.FromSeconds(5);
            o.CircuitBreakerThreshold = 100;   // high so retries don't trip the CB
            o.CircuitBreakerWindow = TimeSpan.FromSeconds(30);
            if (retryDelays is not null)
            {
                o.RetryDelays = retryDelays;
            }
            o.Services.Add("service-x", new ServiceEndpoint { BaseUrl = new Uri("http://service-x.test") });
        });
        services.AddSingleton<IValidateOptions<HttpTransportOptions>, HttpTransportOptionsValidator>();
        services.AddSingleton<HmacSignatureService>();
        services.AddSingleton<NonceDedupeCache>(sp =>
            new NonceDedupeCache(sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.NonceCacheCapacity));
        services.AddSingleton<MessageIdDedupeCache>(sp =>
            new MessageIdDedupeCache(sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.MessageIdCacheCapacity));
        services.AddSingleton<HmacAuthorizationFilter>();
        services.AddSingleton<HttpTransportReceiver>();
        PollyResilienceHandlerSetup.RegisterNamedClients(services);
        services.AddHttpClient(PollyResilienceHandlerSetup.NamedClientPrefix)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.TryAddSingleton<global::UKBatch.Transport.Http.HttpTransport>(sp => new global::UKBatch.Transport.Http.HttpTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>(),
            sp.GetRequiredService<HttpTransportReceiver>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<global::UKBatch.Transport.Http.HttpTransport>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IServiceDiscovery>()));
        var sp = services.BuildServiceProvider();
        var transport = sp.GetRequiredService<global::UKBatch.Transport.Http.HttpTransport>();
        return (transport, sp, handler);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<HttpResponseMessage> Script { get; } = new();
        public List<string> NonceHeaders { get; } = new();
        public List<string> TimestampHeaders { get; } = new();
        public int CallCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            NonceHeaders.Add(request.Headers.TryGetValues("X-UKBatch-Nonce", out var n) ? string.Join(",", n) : "<missing>");
            TimestampHeaders.Add(request.Headers.TryGetValues("X-UKBatch-Timestamp", out var t) ? string.Join(",", t) : "<missing>");
            if (CallCount > Script.Count)
            {
                // Default to 200 if script is exhausted.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            var resp = Script[CallCount - 1];
            if (resp.StatusCode == 0)
            {
                throw new HttpRequestException("scripted transient");
            }
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task PollyRetry_5xx_RetriedUpToConfiguredCount()
    {
        var handler = new ScriptedHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.Script.Add(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
            h.CallCount.Should().Be(4, "Polly retries 3 times after the initial 5xx then accepts the 4th call");
        }
    }

    [Fact]
    public async Task PollyRetry_4xx_NotRetried()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.BadRequest));
        // No more scripts — if Polly retried, CallCount would > 1.
        var (transport, sp, h) = Build(handler);
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.PublishAsync(BuildMessage(), CancellationToken.None);
            await act.Should().ThrowAsync<HttpRequestException>();
            h.CallCount.Should().Be(1, "4xx must NOT trigger Polly retry");
        }
    }

    [Fact]
    public async Task PollyRetry_HttpRequestException_Retried()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage((HttpStatusCode)0)); // sentinel for throw
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
            h.CallCount.Should().Be(2);
        }
    }

    [Fact]
    public async Task PollyRetry_TaskCanceledException_NotRetried_CallerIntent()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.OK)); // never reached
        var (transport, sp, h) = Build(handler);
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Func<Task> act = () => transport.PublishAsync(BuildMessage(), cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
            // Polly does NOT retry on caller-side cancellation — caller intent honored.
            h.CallCount.Should().BeLessThanOrEqualTo(1);
        }
    }

    [Fact]
    public async Task PollyRetry_5xxThenSuccess_PollyRecovers()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
            h.CallCount.Should().Be(2);
        }
    }

    [Fact]
    public async Task PollyRetry_ExhaustsBudget_ThrowsHttpRequestException()
    {
        var handler = new ScriptedHandler();
        // 4 attempts (initial + 3 retries) all fail with 503.
        for (var i = 0; i < 4; i++)
        {
            handler.Script.Add(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.PublishAsync(BuildMessage(), CancellationToken.None);
            await act.Should().ThrowAsync<HttpRequestException>();
            h.CallCount.Should().Be(4);
        }
    }

    [Fact]
    public async Task PollyRetry_408RequestTimeout_Retried()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.RequestTimeout));
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
            h.CallCount.Should().Be(2, "408 RequestTimeout triggers Polly retry");
        }
    }

    [Fact]
    public async Task PollyRetry_503ServiceUnavailable_Retried()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
            h.CallCount.Should().Be(2);
        }
    }

    [Fact]
    public async Task PollyRetry_400BadRequest_NotRetried()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var (transport, sp, h) = Build(handler);
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.PublishAsync(BuildMessage(), CancellationToken.None);
            await act.Should().ThrowAsync<HttpRequestException>();
            h.CallCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task PollyRetry_404NotFound_NotRetried()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.NotFound));
        var (transport, sp, h) = Build(handler);
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.PublishAsync(BuildMessage(), CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
            h.CallCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task PollyRetry_DelayBetweenAttempts_RoughlyMatchesConfig()
    {
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        // Configure a 100ms delay; observe at least 100ms gap between attempts.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(100) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
        }
        sw.Stop();
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50),
            "Polly applied the configured retry delay between attempts (with jitter slack)");
    }

    // regression lock
    [Fact]
    public async Task HttpTransport_Polly_Retry_NonceRotatesPerAttempt_NoReplay401()
    {
        var handler = new ScriptedHandler();
        // 5xx twice → success on third call. All three attempts MUST carry distinct nonces.
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
            h.CallCount.Should().Be(3);
            h.NonceHeaders.Should().HaveCount(3);
            h.NonceHeaders.Distinct().Count().Should().Be(3,
 "each Polly retry attempt MUST sign with a FRESH nonce so the receiver's NonceDedupeCache does NOT reject as replay");
            // Timestamps may match if attempts are < 1 ms apart, but with retry delay > 0 they should differ in most cases.
            // We don't assert distinct timestamps as a strict requirement — the nonce rotation is the operative invariant.
        }
    }

    [Fact]
    public async Task PollyRetry_OnRetry_HasOpportunityToObserveAttempts()
    {
        // Sanity test that the retry observer fires; we don't directly inspect logs but the call
        // count proves Polly's retry loop ran.
        var handler = new ScriptedHandler();
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Script.Add(new HttpResponseMessage(HttpStatusCode.Accepted));
        var (transport, sp, h) = Build(handler, retryDelays: new[] { TimeSpan.FromMilliseconds(10) });
        await using (sp.ConfigureAwait(false))
        {
            await transport.PublishAsync(BuildMessage(), CancellationToken.None);
            h.CallCount.Should().Be(2);
        }
    }
}
