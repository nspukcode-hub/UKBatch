using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Transport;

/// <summary>
/// Pluggable cross-service messaging abstraction. Adapter implementations include in-process,
/// HTTP, RabbitMQ, Kafka, and Azure Service Bus. Implementations MUST be thread-safe.
/// </summary>
public interface ITransport
{
    /// <summary>Logical transport name (e.g. <c>"InProcess"</c>, <c>"Http"</c>, <c>"RabbitMQ"</c>). Surfaced in diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Fire-and-forget publish. Delivery semantics (at-most-once vs at-least-once vs exactly-once)
    /// are adapter-specific and documented per adapter package.
    /// </summary>
    Task PublishAsync(JobMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to a topic and yields messages as they arrive. Cancellation gracefully unsubscribes
    /// and disposes adapter-side resources.
    /// </summary>
    IAsyncEnumerable<JobMessage> SubscribeAsync(string topic, CancellationToken cancellationToken);

    /// <summary>
    /// Request/reply over the transport. Adapters that cannot natively support reply (e.g.
    /// fire-and-forget message bus) emulate via correlation id and reply queue. Throws
    /// <see cref="TimeoutException"/> if the reply does not arrive within <paramref name="timeout"/>.
    /// </summary>
    Task<JobResult> RequestReplyAsync(string targetService, JobMessage message, TimeSpan timeout, CancellationToken cancellationToken);
}
