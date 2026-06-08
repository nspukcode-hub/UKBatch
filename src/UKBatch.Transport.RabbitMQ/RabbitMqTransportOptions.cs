namespace UKBatch.Transport.RabbitMQ;

/// <summary>
/// Configuration surface for <see cref="RabbitMqTransport"/>. Bind from
/// <c>UKBatch:Transport:RabbitMQ</c> under
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>. Validated via
/// <see cref="RabbitMqTransportOptionsValidator"/> at host start — invalid configuration fails
/// <c>IHost.StartAsync</c>.
/// </summary>
/// <remarks>
/// <para><b>Connection:</b> supply EITHER <see cref="Uri"/> (full AMQP URI, takes precedence) OR the
/// discrete <see cref="HostName"/> / <see cref="Port"/> / <see cref="VirtualHost"/> /
/// <see cref="UserName"/> / <see cref="Password"/> / <see cref="UseTls"/> fields. Mixing a non-default
/// <see cref="Uri"/> with non-default discrete fields fails validation (ambiguous source of truth).</para>
/// <para><b>Security:</b> there is NO application-level HMAC. Authentication and confidentiality
/// live at the broker layer — provision a non-<c>guest</c> user/password and enable
/// <see cref="UseTls"/> (or an <c>amqps://</c> <see cref="Uri"/>) in production.</para>
/// <para><b>Mutability:</b> bound once at registration. <c>IOptionsMonitor</c> reload is NOT honored in
/// v0.1 — restart the host to apply changes.</para>
/// </remarks>
public sealed class RabbitMqTransportOptions
{
    // ===== Connection (Uri XOR discrete fields) =====

    /// <summary>
    /// Full AMQP connection URI (<c>amqp://user:pass@host:5672/vhost</c> or <c>amqps://...</c>). When
    /// set to a non-empty value it takes precedence over the discrete connection fields.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>Broker host name. Default <c>localhost</c>. Ignored when <see cref="Uri"/> is set.</summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>Broker AMQP port. Default <c>5672</c> (TLS: <c>5671</c>). Ignored when <see cref="Uri"/> is set.</summary>
    public int Port { get; set; } = 5672;

    /// <summary>AMQP virtual host. Default <c>/</c>. Ignored when <see cref="Uri"/> is set.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Broker user name. Default <c>guest</c> (dev only). Ignored when <see cref="Uri"/> is set.</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>Broker password. Default <c>guest</c> (dev only). Ignored when <see cref="Uri"/> is set.</summary>
    public string Password { get; set; } = "guest";

    /// <summary>Enable TLS (AMQPS) for the discrete-field connection. Ignored when <see cref="Uri"/> is set.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// Opt-in escape hatch for the default <c>guest</c>/<c>guest</c> credentials on a NON-loopback broker
    /// host. Default <c>false</c>: host start FAILS when a non-loopback broker is reached with the default
    /// credentials, because there is no application-level HMAC on this transport — the broker layer is the
    /// only authentication boundary, so default credentials reachable off-box are a real exposure. Set to
    /// <c>true</c> to acknowledge an internal/trusted-network broker explicitly. Loopback hosts (localhost,
    /// 127.0.0.1, ::1) are always exempt. TLS (<see cref="UseTls"/> / <c>amqps://</c>) is independently
    /// recommended for any non-loopback broker but is not enforced by this flag.
    /// </summary>
    public bool AllowInsecureBroker { get; set; }

    // ===== Topology =====

    /// <summary>Direct, durable job exchange. Default <c>ukbatch.jobs</c>. Routing key = target service name.</summary>
    public string ExchangeName { get; set; } = "ukbatch.jobs";

    /// <summary>Fanout, durable dead-letter exchange. Default <c>ukbatch.jobs.dlx</c>.</summary>
    public string DeadLetterExchangeName { get; set; } = "ukbatch.jobs.dlx";

    /// <summary>Durable dead-letter queue bound to <see cref="DeadLetterExchangeName"/>. Default <c>ukbatch.dlq</c>.</summary>
    public string DeadLetterQueueName { get; set; } = "ukbatch.dlq";

    /// <summary>
    /// Prefix for this node's durable service queue. Final name =
    /// <c>{QueuePrefix}{ThisServiceName}</c>. Default <c>ukbatch.service.</c>.
    /// </summary>
    public string QueuePrefix { get; set; } = "ukbatch.service.";

    // ===== Behavior =====

    /// <summary>
    /// Consumer prefetch (QoS) count — max unacked deliveries in flight. Default <c>16</c>. Recommended
    /// to track the host's max concurrent job slots.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 16;

    /// <summary>
    /// Broker-enforced delivery limit on the (quorum) service queue (<c>x-delivery-limit</c>).
    /// On exceeding it the broker dead-letters the message automatically. Default <c>5</c>.
    /// </summary>
    public int MaxRedeliveryCount { get; set; } = 5;

    /// <summary>Default wall-clock timeout for <see cref="RabbitMqTransport.RequestReplyAsync"/>. Default <c>30s</c>.</summary>
    public TimeSpan DefaultRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reserved for a future release: an explicit per-publish confirm-wait bound. Today the publish path
    /// relies on the client library's built-in publisher-confirmation tracking and the caller's
    /// <see cref="System.Threading.CancellationToken"/>; this value is validated but is NOT yet applied
    /// to the confirm wait. Default <c>10s</c>.
    /// </summary>
    public TimeSpan PublisherConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Capacity of the in-memory receiver-side MessageId dedupe cache (effectively-once). Default
    /// <c>4096</c>. Persistent dedupe is a v0.2 concern.
    /// </summary>
    public int MessageIdCacheCapacity { get; set; } = 4096;

    /// <summary>
    /// Per-consumer-channel dispatch concurrency. <b>v0.1: MUST be 1</b> (validator-enforced)
    /// — the consumer <c>IChannel</c> is not thread-safe and the reply-publish + ack would race / corrupt
    /// frames at &gt; 1. Scale throughput via <see cref="PrefetchCount"/> and/or multiple worker instances;
    /// per-channel concurrency is a v0.2 concern.
    /// </summary>
    public ushort ConsumerDispatchConcurrency { get; set; } = 1;

    // ===== Resilience (initial connect) =====

    /// <summary>
    /// Retry backoff schedule for the initial broker connect. <c>null</c> ⇒ default
    /// <c>[2s, 5s, 15s]</c> + jitter. Broker auto-recovery handles reconnects after the first connect.
    /// </summary>
    public IReadOnlyList<TimeSpan>? RetryDelays { get; set; }

    /// <summary>Circuit-breaker failure threshold for the initial connect pipeline. Default <c>5</c>.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Circuit-breaker sampling + open window. Default <c>30s</c>.</summary>
    public TimeSpan CircuitBreakerWindow { get; set; } = TimeSpan.FromSeconds(30);
}
