using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Receiver;
using UKBatch.Transport.Http.Resilience;

namespace UKBatch.Transport.Http;

/// <summary>
/// HTTP-based <see cref="ITransport"/> adapter. Wire protocol: POST + GET under
/// <c>/ukbatch/internal/jobs/*</c>. Auth: HMAC SHA256 over canonical envelope
/// (see <see cref="HmacCanonicalForm"/>). Resilience: Polly retry + circuit breaker +
/// per-request timeout.
/// </summary>
/// <remarks>
/// <para>Thread-safe. Single-instance singleton — multiple concurrent
/// <see cref="PublishAsync"/> / <see cref="RequestReplyAsync"/> calls share the underlying
/// <see cref="HttpClient"/> via <see cref="IHttpClientFactory"/> named clients.</para>
/// </remarks>
public sealed class HttpTransport : ITransport
{
    private const string PublishPath = "/ukbatch/internal/jobs/publish";
    private const string PollPath = "/ukbatch/internal/jobs/poll";
    private const string InvokePath = "/ukbatch/internal/jobs/invoke";

    /// <summary>
    /// JSON options for outbound serialization + inbound deserialization. Includes
    /// <see cref="JsonStringEnumConverter"/> so <see cref="JobResult.Status"/> round-trips
    /// against workers that host <c>AddUKBatchApi</c> (which configures Web-default
    /// <c>ConfigureHttpJsonOptions</c> emitting string enums). Without it, the integer
    /// the worker would emit fails to parse here.
    /// </summary>
    /// <remarks>Internal visibility for the regression test that locks the converter presence.</remarks>
    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _factory;
    private readonly IOptions<HttpTransportOptions> _options;
    private readonly IServiceDiscovery? _serviceDiscovery;
    private readonly HttpTransportReceiver _receiver;
    private readonly ILogger<HttpTransport> _logger;
    private readonly TimeProvider _timeProvider;

    internal HttpTransport(
        IHttpClientFactory factory,
        IOptions<HttpTransportOptions> options,
        HttpTransportReceiver receiver,
        ILogger<HttpTransport> logger,
        TimeProvider timeProvider,
        IServiceDiscovery? serviceDiscovery = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _factory = factory;
        _options = options;
        _receiver = receiver;
        _logger = logger;
        _timeProvider = timeProvider;
        _serviceDiscovery = serviceDiscovery;   // v0.1: always null until v0.2 adapter ships
    }

    /// <inheritdoc/>
    public string Name => "Http";

    /// <inheritdoc/>
    public async Task PublishAsync(JobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.TargetService is null)
        {
            throw new InvalidOperationException(
                "PublishAsync requires JobMessage.TargetService to be non-null over HTTP transport.");
        }

        var endpoint = await ResolveEndpointAsync(message.TargetService, cancellationToken).ConfigureAwait(false);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);

        using var request = BuildSignedRequest(
            HttpMethod.Post,
            endpoint.BaseUrl,
            PublishPath,
            queryParams: null,
            bodyBytes);

        var client = GetClient();
        using var response = await SendAsync(client, request, additionalTimeoutHeader: null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForFailedResponseAsync("publish", message.TargetService, response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<JobMessage> SubscribeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);

        // Worker-side: in-process consume path — receiver is the source of truth.
        // Cross-service subscribe via long-poll requires the topic to map to a registered Service;
        // when that mapping is missing, we yield from the in-process receiver pump (matches
        // InProcessTransport semantics for tests / co-located worker pattern).
        if (!_options.Value.Services.TryGetValue(topic, out var endpoint))
        {
            await foreach (var msg in _receiver.ConsumeAsync(topic, cancellationToken).ConfigureAwait(false))
            {
                yield return msg;
            }
            yield break;
        }

        var client = GetClient();
        var longPollWait = _options.Value.LongPollMaxWait;

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpResponseMessage? response = null;
            try
            {
                var queryParams = new[]
                {
                    new KeyValuePair<string, IReadOnlyList<string>>(
                        "topic", new[] { topic }),
                    new KeyValuePair<string, IReadOnlyList<string>>(
                        "waitMs", new[] { longPollWait.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) }),
                };

                using var request = BuildSignedRequest(
                    HttpMethod.Get,
                    endpoint.BaseUrl,
                    PollPath,
                    queryParams,
                    bodyBytes: ReadOnlyMemory<byte>.Empty);

                response = await SendAsync(client, request, additionalTimeoutHeader: null, cancellationToken).ConfigureAwait(false);

                // A long-poll cancelled mid-flight can come back as a client-abort status
                // (e.g. 499) instead of throwing OperationCanceledException, depending on which
                // side observes the cancellation first. Treat a cancelled subscription as a
                // graceful stop regardless of how the abort surfaced, rather than letting it
                // bubble up as a spurious transport error.
                if (cancellationToken.IsCancellationRequested)
                {
                    response.Dispose();
                    yield break;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException(
                        "HttpTransport.SubscribeAsync received 401 Unauthorized — HMAC signature mismatch or clock skew.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    // Polly already exhausted retries for transient errors; surface the failure.
                    await ThrowForFailedResponseAsync("poll", topic, response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                yield break;
            }

            PollResponse? body;
            try
            {
                body = await response!.Content
                    .ReadFromJsonAsync<PollResponse>(JsonOpts, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                response!.Dispose();
            }

            if (body?.Messages is null || body.Messages.Count == 0)
            {
                continue;
            }

            foreach (var msg in body.Messages)
            {
                yield return msg;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<JobResult> RequestReplyAsync(
        string targetService,
        JobMessage message,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetService);
        ArgumentNullException.ThrowIfNull(message);

        var endpoint = await ResolveEndpointAsync(targetService, cancellationToken).ConfigureAwait(false);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);

        // Linked CT: caller CT + caller-supplied timeout. HttpClient.Timeout is INFINITE
        // (Polly is the authoritative timeout); this is the wall-clock cap for THIS call.
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var request = BuildSignedRequest(
            HttpMethod.Post,
            endpoint.BaseUrl,
            InvokePath,
            queryParams: null,
            bodyBytes);
        request.Headers.TryAddWithoutValidation(
            HmacHeaderNames.TimeoutMs,
            ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));

        var client = GetClient();
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(client, request, additionalTimeoutHeader: null, linked.Token).ConfigureAwait(false);
        }
        catch (TimeoutRejectedException)
        {
            throw new TimeoutException(
                $"RequestReplyAsync to '{targetService}' exceeded Polly outer timeout {_options.Value.DefaultRequestTimeout}.");
        }
        catch (TaskCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"RequestReplyAsync to '{targetService}' timed out after {timeout}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await ThrowForFailedResponseAsync("invoke", targetService, response, cancellationToken).ConfigureAwait(false);
            }

            var jobResult = await response.Content
                .ReadFromJsonAsync<JobResult>(JsonOpts, cancellationToken)
                .ConfigureAwait(false);
            if (jobResult is null)
            {
                throw new InvalidOperationException(
                    $"RequestReplyAsync to '{targetService}' returned 200 but the body was empty / unreadable.");
            }
            return jobResult;
        }
    }

    /// <summary>
    /// Resolves a <see cref="ServiceEndpoint"/> for the given logical service name. v0.1: static dict
    /// from <see cref="HttpTransportOptions.Services"/>. v0.2+: <see cref="IServiceDiscovery"/> takes
    /// precedence when registered.
    /// </summary>
    private async ValueTask<ServiceEndpoint> ResolveEndpointAsync(string serviceName, CancellationToken ct)
    {
        if (_serviceDiscovery is not null)
        {
            var dynamic = await _serviceDiscovery.ResolveAsync(serviceName, ct).ConfigureAwait(false);
            if (dynamic is not null)
            {
                return dynamic;
            }
        }
        if (_options.Value.Services.TryGetValue(serviceName, out var endpoint))
        {
            return endpoint;
        }
        throw new InvalidOperationException(
            $"Unknown service '{serviceName}' — not in HttpTransportOptions.Services.");
    }

    private HttpClient GetClient() => _factory.CreateClient(PollyResilienceHandlerSetup.NamedClientPrefix);

    /// <summary>
    /// Builds an UNSIGNED <see cref="HttpRequestMessage"/> with the canonical-path slot attached.
    /// <see cref="HmacSigningHandler"/> signs the envelope per Polly attempt, so the
    /// nonce + timestamp ROTATE on retry.
    /// </summary>
    /// <remarks>
    /// HMAC header attachment moved from this method to <see cref="HmacSigningHandler.SendAsync"/>
    /// so each Polly retry sees a fresh nonce. The canonical path is computed once here (path +
    /// query are stable across attempts) and stored on the request via
    /// <see cref="HmacSigningHandler.AttachCanonicalPath"/>.
    /// </remarks>
    private static HttpRequestMessage BuildSignedRequest(
        HttpMethod method,
        Uri baseUrl,
        string absolutePath,
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>>? queryParams,
        ReadOnlyMemory<byte> bodyBytes)
    {
        var canonicalPath = HmacCanonicalForm.BuildCanonicalPathForSender(absolutePath, queryParams);

        // Build the relative URL — concatenate baseUrl path with absolutePath. baseUrl normally
        // ends at the host; HttpClient + BaseAddress will combine. We use AbsoluteUri so the
        // explicit path replaces any path on the base (which has none for ukbatch transport).
        var fullUri = new Uri(new Uri(baseUrl.GetLeftPart(UriPartial.Authority)), BuildUrlWithQuery(absolutePath, queryParams));

        var request = new HttpRequestMessage(method, fullUri);

        // Attach canonical path slot — HmacSigningHandler reads this to rebuild the envelope
        // before sending (per Polly attempt). HMAC headers themselves are set by the handler.
        HmacSigningHandler.AttachCanonicalPath(request, canonicalPath);

        if (method != HttpMethod.Get && bodyBytes.Length > 0)
        {
            var content = new ByteArrayContent(bodyBytes.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            request.Content = content;
        }
        return request;
    }

    private static string BuildUrlWithQuery(
        string absolutePath,
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>>? queryParams)
    {
        if (queryParams is null || queryParams.Count == 0)
        {
            return absolutePath;
        }
        var sb = new System.Text.StringBuilder(absolutePath.Length + 64);
        sb.Append(absolutePath);
        sb.Append('?');
        var first = true;
        foreach (var kv in queryParams)
        {
            foreach (var v in kv.Value)
            {
                if (!first) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(v ?? string.Empty));
                first = false;
            }
        }
        return sb.ToString();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan? additionalTimeoutHeader,
        CancellationToken ct)
    {
        _ = additionalTimeoutHeader; // reserved for v0.2 per-call timeout propagation
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex,
                "HttpTransport: circuit breaker open ({Method} {Path}).",
                request.Method.Method, request.RequestUri?.AbsolutePath);
            throw;
        }
    }

    private static async Task ThrowForFailedResponseAsync(
        string operation,
        string serviceOrTopic,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var bodySnippet = "";
        try
        {
            bodySnippet = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore — body read is diagnostic only
        }
        var maxLen = Math.Min(bodySnippet.Length, 512);
        var truncated = bodySnippet.Length > 0 ? bodySnippet[..maxLen] : "<empty>";

        if (status == 401)
        {
            throw new InvalidOperationException(
                $"HTTP transport {operation} to '{serviceOrTopic}' rejected with 401 Unauthorized (HMAC verify failed). Body: {truncated}");
        }
        if (status == 404)
        {
            throw new InvalidOperationException(
                $"HTTP transport {operation} to '{serviceOrTopic}' returned 404 — receiver could not locate the job. Body: {truncated}");
        }
        if (status == 408)
        {
            throw new TimeoutException(
                $"HTTP transport {operation} to '{serviceOrTopic}' returned 408 — receiver-side timeout. Body: {truncated}");
        }
        // 4xx other / 5xx after retries:
        throw new HttpRequestException(
            $"HTTP transport {operation} to '{serviceOrTopic}' returned {status}. Body: {truncated}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>Wire envelope for <c>GET /poll</c> responses.</summary>
    private sealed class PollResponse
    {
        public List<JobMessage> Messages { get; set; } = new();
    }
}
