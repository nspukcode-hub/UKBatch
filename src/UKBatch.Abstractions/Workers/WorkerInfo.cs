namespace UKBatch.Abstractions.Workers;

/// <summary>
/// Server-side snapshot of a known worker, returned by <c>GET /api/workers</c> and rendered in the
/// dashboard Workers panel. Built from the most recent <see cref="WorkerBeatRequest"/> the registry
/// received, with the live <see cref="Online"/> flag computed at snapshot time.
/// </summary>
/// <remarks>
/// Pure POCO wire contract (mirrors <see cref="Transport.JobMessage"/> / <see cref="Models.ProgressBeat"/>
/// placement). A row whose <see cref="Online"/> is <c>false</c> is a recently-departed worker retained
/// until the hard-evict horizon, so the panel can show "offline · last seen 2m ago" rather than the row
/// vanishing. <see cref="Status"/> crosses the wire as a string.
/// </remarks>
public sealed record class WorkerInfo
{
    /// <summary>Logical worker/service name (the registry key — see <see cref="WorkerBeatRequest.Name"/>).</summary>
    public required string Name { get; init; }

    /// <summary>Names of the jobs this worker advertised on its last beat.</summary>
    public IReadOnlyList<string> Jobs { get; init; } = [];

    /// <summary>Free-form tags this worker advertised on its last beat.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Last advertised lifecycle state. Defaults to <see cref="WorkerStatus.Offline"/> (the safe state
    /// for a row that has aged out or whose state is otherwise unknown).
    /// </summary>
    public WorkerStatus Status { get; init; } = WorkerStatus.Offline;

    /// <summary>UTC timestamp of the most recent beat the server received from this worker.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>
    /// <c>true</c> when the worker is considered alive at snapshot time: its last <see cref="Status"/>
    /// was <see cref="WorkerStatus.Online"/> AND <see cref="LastSeenUtc"/> is within the online TTL.
    /// An explicit <see cref="WorkerStatus.Offline"/> beat (graceful stop) flips this to <c>false</c>
    /// immediately, even within the TTL window.
    /// </summary>
    public bool Online { get; init; }
}
