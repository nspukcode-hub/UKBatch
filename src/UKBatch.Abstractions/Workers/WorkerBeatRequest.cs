namespace UKBatch.Abstractions.Workers;

/// <summary>
/// Wire-format payload a worker POSTs to <c>/api/workers/beat</c> on each heartbeat tick.
/// Observability only — the server NEVER consults a beat to make a dispatch decision; the orchestrator
/// reaches a worker over the configured <c>ITransport</c>, not via this registry.
/// </summary>
/// <remarks>
/// Pure POCO wire contract (mirrors <see cref="Transport.JobMessage"/> / <see cref="Models.ProgressBeat"/>
/// placement). Producer: <c>UKBatch.Worker</c>'s heartbeat background service. Consumer:
/// <c>UKBatch.Api</c>'s worker registry. <see cref="Status"/> crosses the wire as a string.
/// </remarks>
public sealed record class WorkerBeatRequest
{
    /// <summary>
    /// Logical worker/service name. REQUIRED. This is the registry key AND the routing key — it MUST
    /// match (ordinal) the <c>TargetService</c> the orchestrator routes to (a mismatch is silent: the
    /// dispatch waits durably or is refused, and this observability panel will not reveal it).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Names of the jobs this worker has registered. Empty when the worker hosts no jobs.</summary>
    public IReadOnlyList<string> Jobs { get; init; } = [];

    /// <summary>Free-form tags surfaced in the dashboard Workers panel (e.g. <c>["eu-west","gpu"]</c>).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Advertised lifecycle state. Defaults to <see cref="WorkerStatus.Online"/>.</summary>
    public WorkerStatus Status { get; init; } = WorkerStatus.Online;

    /// <summary>
    /// In-flight job count at the moment of the beat. v0.1 always reports <c>0</c> (not yet wired to
    /// dispatcher counters); reserved for v0.2.
    /// </summary>
    public int InFlight { get; init; }

    /// <summary>
    /// Advertised concurrency capacity. v0.1 always reports <c>0</c> (<c>0</c> = "unknown/unbounded");
    /// reserved for v0.2.
    /// </summary>
    public int Capacity { get; init; }
}
