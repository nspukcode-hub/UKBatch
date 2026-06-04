namespace UKBatch.Transport.Http;

/// <summary>
/// Configuration surface for <see cref="HttpTransport"/>. Bind from <c>UKBatch:Transport:Http</c>
/// section under <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>. Validated via
/// <see cref="HttpTransportOptionsValidator"/> at host start — invalid configuration fails
/// <c>IHost.StartAsync</c>.
/// </summary>
/// <remarks>
/// <para><b>Mutability:</b> bound once at registration.
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/> reload IS NOT supported for
/// <see cref="SharedSecret"/> / <see cref="Services"/> in v0.1; resilience-pipeline rebuilds on
/// reload are out of scope. Restart the host to apply changes.</para>
/// <para><b>Secret handling:</b> <see cref="SharedSecret"/> MUST come from a secure source (env var,
/// Azure Key Vault, AWS Secrets Manager). Storing in plaintext <c>appsettings.json</c> is acceptable
/// only for local dev / sample apps. The validator does NOT inspect entropy or length; deployment
/// owners are responsible for choosing a 32+ byte secret.</para>
/// </remarks>
public sealed class HttpTransportOptions
{
    /// <summary>
    /// Per-service endpoint registry. Key = logical service name (matches
    /// <see cref="UKBatch.Abstractions.Transport.JobMessage.TargetService"/>); value = endpoint metadata.
    /// </summary>
    /// <remarks>
    /// Mutated by configuration binder (.NET <c>ConfigurationBinder</c> requires a settable Dictionary
    /// property, not <c>IReadOnlyDictionary</c>). Validator enforces non-empty keys + absolute URI per entry.
    /// </remarks>
    public IDictionary<string, ServiceEndpoint> Services { get; set; }
        = new Dictionary<string, ServiceEndpoint>(StringComparer.Ordinal);

    /// <summary>
    /// Shared symmetric secret for HMAC SHA256 signing. MUST be non-empty if <see cref="Services"/>
    /// is non-empty (sender side) OR if the receiver mounts
    /// <see cref="EndpointRouteBuilderExtensions.MapUKBatchHttpTransport"/>. Both sender and
    /// receiver MUST be configured with the same secret.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>
    /// Per-request HTTP wall-clock timeout for <see cref="HttpTransport.RequestReplyAsync"/> +
    /// <see cref="HttpTransport.PublishAsync"/>. Default 30 seconds. NOT applied to
    /// <see cref="HttpTransport.SubscribeAsync"/> (which uses <see cref="LongPollMaxWait"/>).
    /// </summary>
    /// <remarks>
    /// Validator: must be &gt; <see cref="TimeSpan.Zero"/> and &lt;= 10 minutes. Caller's
    /// <see cref="CancellationToken"/> remains authoritative — this is the upper bound on a single
    /// request, not the entire retry budget.
    /// </remarks>
    public TimeSpan DefaultRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Server-side cap on <c>GET /ukbatch/internal/jobs/poll</c> hold duration. Default 30 seconds.
    /// </summary>
    /// <remarks>
    /// Sender's HTTP timeout MUST exceed this value (validator enforces
    /// <see cref="DefaultRequestTimeout"/> &gt;= <see cref="LongPollMaxWait"/> + 5 seconds slack).
    /// Otherwise the sender's HTTP timeout fires before the server returns the empty array,
    /// producing false-positive <see cref="HttpRequestException"/>.
    /// </remarks>
    public TimeSpan LongPollMaxWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Custom retry delays for the Polly retry policy. <c>null</c> = use default
    /// <c>[2s, 5s, 15s]</c> + jitter (10% of delay). Set explicitly to override.
    /// </summary>
    public IReadOnlyList<TimeSpan>? RetryDelays { get; set; }

    /// <summary>
    /// Circuit breaker failure threshold within <see cref="CircuitBreakerWindow"/>. Default 5.
    /// Validator: must be &gt;= 1.
    /// </summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>
    /// Circuit breaker sampling window. Default 30 seconds. After threshold breach, the circuit
    /// opens for the same duration before transitioning to half-open. Validator: must be &gt;= 1s.
    /// </summary>
    public TimeSpan CircuitBreakerWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum clock skew tolerated between sender + receiver for HMAC timestamp validation.
    /// Default 300 seconds (5 minutes). Larger windows widen the replay-attack opportunity;
    /// smaller windows risk false rejections during NTP drift events. Validator: must be &gt;= 1s
    /// and &lt;= 1 hour.
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Nonce LRU cache capacity for replay prevention. Default 1024 (≈ 10× the timestamp window's
    /// theoretical request rate). Validator: must be &gt;= 16.
    /// </summary>
    public int NonceCacheCapacity { get; set; } = 1024;

    /// <summary>
    /// Receiver-side MessageId dedupe LRU cache capacity. Default 4096
    /// (4× nonce cache because MessageId lifetime spans sender-side retries).
    /// Validator: must be &gt;= 64.
    /// </summary>
    /// <remarks>
    /// Sender retry rotates nonce per attempt but keeps the original
    /// <see cref="UKBatch.Abstractions.Transport.JobMessage.MessageId"/>.
    /// Receiver-side <see cref="NonceCacheCapacity"/> blocks signature replay (good), but does NOT
    /// block message duplication. Core transport invariant: receivers de-duplicate on this id.
    /// </remarks>
    public int MessageIdCacheCapacity { get; set; } = 4096;

    /// <summary>
    /// Maximum inbound body size (bytes) that the HMAC filter will buffer for signature verification.
    /// Default 1 MB. Larger requests are rejected with HTTP 413 BEFORE the handler runs — bounds
    /// HMAC body-hash CPU + memory cost so adversarial clients cannot DoS the receiver.
    /// </summary>
    /// <remarks>
    /// Operator MUST ensure Kestrel's <c>Limits.MaxRequestBodySize</c> is &gt;= this value;
    /// otherwise Kestrel rejects with 413 first and the filter never observes the request.
    /// Default Kestrel cap is 30 MB which exceeds the default 1 MB here.
    /// </remarks>
    public int MaxBodyBytes { get; set; } = 1_048_576;
}
