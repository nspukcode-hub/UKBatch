using UKBatch.Abstractions.Jobs;

namespace UKBatch.Abstractions.Workers;

/// <summary>
/// One job a worker advertises on its heartbeat, carrying the job's declared parameters so the
/// dashboard can offer a typed hint for a remote (cross-service) job the orchestrator does not host
/// in-process. Observability only — never consulted for dispatch.
/// </summary>
public sealed record class WorkerJobDescriptor
{
    /// <summary>The advertised job name (matches an entry in <see cref="WorkerBeatRequest.Jobs"/>).</summary>
    public required string Name { get; init; }

    /// <summary>The job's declared parameters (empty when the job declares none).</summary>
    public IReadOnlyList<JobParameterDescriptor> Parameters { get; init; } = [];
}
