namespace UKBatch.Abstractions.Transport;

/// <summary>Wire-format envelope for a transported job invocation.</summary>
public sealed record class JobMessage
{
    /// <summary>Idempotency key. Receivers de-duplicate on this id.</summary>
    public required string MessageId { get; init; }

    /// <summary>Optional id correlating responses with requests (used by <see cref="ITransport.RequestReplyAsync"/>).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Logical job name on the target service.</summary>
    public required string JobName { get; init; }

    /// <summary>Originating service name; receivers may use this for callback routing.</summary>
    public required string SourceService { get; init; }

    /// <summary>Target service name; <c>null</c> means broadcast on the topic.</summary>
    public string? TargetService { get; init; }

    /// <summary>Optional batch id this message participates in.</summary>
    public string? BatchId { get; init; }

    /// <summary>Optional batch step id this message satisfies.</summary>
    public string? BatchStepId { get; init; }

    /// <summary>Job parameters; values MUST be JSON-serializable for wire interop.</summary>
    public required IReadOnlyDictionary<string, object?> Parameters { get; init; }

    /// <summary>
    /// Wire-format headers — auth, signing, tracing.
    /// </summary>
    /// <remarks>
    /// Transport adapters MUST preserve all header keys round-trip. Reserved keys (case-insensitive):
    /// <list type="bullet">
    ///   <item><c>traceparent</c>, <c>tracestate</c> — W3C Trace Context (<see cref="System.Diagnostics.Activity"/>).</item>
    ///   <item><c>x-ukbatch-signature</c> — HMAC signature (HTTP transport).</item>
    ///   <item><c>x-ukbatch-idempotency-key</c> — caller-supplied dedup key; mirrors <see cref="MessageId"/> if absent.</item>
    /// </list>
    /// Adapter packages may add their own <c>x-ukbatch-*</c> prefixed keys; consumers MUST NOT depend on
    /// non-reserved keys being preserved by all adapters.
    /// </remarks>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>UTC enqueue timestamp.</summary>
    public required DateTimeOffset EnqueuedAtUtc { get; init; }

    /// <summary>1-based attempt counter for retry tracking.</summary>
    public required int AttemptNumber { get; init; }
}
