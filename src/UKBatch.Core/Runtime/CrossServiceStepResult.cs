using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// The terminal status and produced outputs of one cross-service step dispatch, returned by
/// <see cref="CrossServiceStepInvoker.InvokeAsync"/> so the sequential executor and the parallel-group
/// fan-out can fold a remote step's outputs into the run's accumulated outputs. <see cref="Outputs"/>
/// is non-null ONLY for a <see cref="JobStatus.Completed"/> dispatch — a failed / timed-out / cancelled
/// step produces none.
/// </summary>
internal readonly record struct CrossServiceStepResult(
    JobStatus Status,
    IReadOnlyDictionary<string, object?>? Outputs);
