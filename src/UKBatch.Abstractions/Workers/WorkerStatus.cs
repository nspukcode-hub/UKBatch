namespace UKBatch.Abstractions.Workers;

/// <summary>
/// Lifecycle state a worker advertises via its heartbeat (<see cref="WorkerBeatRequest.Status"/>)
/// and that the server surfaces in the dashboard Workers panel (<see cref="WorkerInfo.Status"/>).
/// </summary>
/// <remarks>
/// Serialized as a string across the wire on BOTH ends (a <c>JsonStringEnumConverter</c> is configured
/// by the worker heartbeat client and by the server's JSON options) — never as the underlying integer.
/// v0.1 only ever sends <see cref="Online"/> (steady-state beat) or <see cref="Offline"/> (graceful stop);
/// <see cref="Draining"/> is reserved for v0.2 graceful-shutdown semantics.
/// </remarks>
public enum WorkerStatus
{
    /// <summary>Worker is alive and accepting dispatch.</summary>
    Online,

    /// <summary>RESERVED (v0.2): worker is finishing in-flight work and will accept no new dispatch.</summary>
    Draining,

    /// <summary>Worker has stopped (or is presumed gone past its TTL).</summary>
    Offline,
}
