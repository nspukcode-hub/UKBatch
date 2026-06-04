namespace UKBatch.Worker;

/// <summary>
/// Configuration for worker self-advertisement. Dispatch is UNAFFECTED by every setting here —
/// the heartbeat is observability-only. Bound from the <c>"UKBatch:Worker"</c> configuration section by
/// <see cref="WorkerModeBuilderExtensions.UseWorkerMode"/>, then overlaid by the
/// <c>Action&lt;WorkerOptions&gt;</c> callback.
/// </summary>
/// <remarks>
/// Mutated by an <c>Action&lt;WorkerOptions&gt;</c> (options pattern), so this is a <c>sealed class</c>
/// with <c>set</c>-able properties — NOT a record. Setters are pure assignment (no side-effects):
/// <see cref="WorkerModeBuilderExtensions.UseWorkerMode"/> invokes the configure callback twice (once
/// eagerly to read <see cref="WorkerName"/>, once via the options pipeline), so the callback MUST be
/// side-effect-free.
/// </remarks>
public sealed class WorkerOptions
{
    /// <summary>
    /// Server base URL for the HTTP heartbeat (e.g. <c>http://ukbatch-server:8080</c>). The heartbeat
    /// POSTs to <c>{ServerUrl}/api/workers/beat</c>. Required when <see cref="Heartbeat"/> is true. MUST
    /// be an absolute URI; trailing slash optional (the named <c>HttpClient</c> base address is normalized
    /// to end with <c>"/"</c>).
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Logical worker/service name. REQUIRED, non-whitespace. Becomes
    /// <c>UKBatchOptions.ThisServiceName</c> (so outbound <c>JobMessage.SourceService</c> is stamped) AND
    /// the registry key on the server. MUST match the <c>TargetService</c> the orchestrator routes to
    /// (HTTP: the <c>HttpTransportOptions.Services</c> key; RabbitMQ: the <c>ukbatch.service.{name}</c>
    /// queue). A mismatch is SILENT.
    /// </summary>
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>Optional free-form tags surfaced in the dashboard Workers panel (e.g. <c>["eu-west","gpu"]</c>).</summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// When true (default), the heartbeat background service is registered and started. When false, no
    /// heartbeat is sent — the worker is invisible in the dashboard Workers panel but dispatch STILL
    /// WORKS (the orchestrator reaches it over the transport). Disable in air-gapped worker→server
    /// topologies where only the orchestrator→worker direction is routable.
    /// </summary>
    public bool Heartbeat { get; set; } = true;

    /// <summary>
    /// Heartbeat cadence. Default 15s. Must be &gt; <see cref="System.TimeSpan.Zero"/> when
    /// <see cref="Heartbeat"/> is true. The server's online TTL is ~3× this (≈45s) before a worker is
    /// marked offline.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// RESERVED for v0.2 worker→server auth. Currently UNUSED — the <c>/api/workers/*</c> endpoints are
    /// auth-agnostic. Set it now and it is silently ignored (no header emitted). Documented as reserved so
    /// the option shape is forward-stable.
    /// </summary>
    public string? ApiKey { get; set; }
}
