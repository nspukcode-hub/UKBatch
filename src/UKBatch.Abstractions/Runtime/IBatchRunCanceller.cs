namespace UKBatch.Abstractions.Runtime;

/// <summary>
/// Cancels an in-flight batch RUN by tripping the run's cancellation token. The endpoint resolving this
/// does NOT need the role that owns a parked approval gate — this is an administrative override whose
/// whole purpose is to kill a run stuck on a gate nobody can decide.
/// </summary>
/// <remarks>
/// <para>Cancelling a run causes a parked approval gate to throw, which the executor propagates WITHOUT
/// running compensation (a cancelled run is not a failed run), and the runtime records the run terminal
/// as <see cref="UKBatch.Abstractions.Models.JobStatus.Cancelled"/>.</para>
/// <para><b>Scope.</b> Cancellation tears down the run orchestration and unblocks a parked gate. An
/// in-flight LOCAL job that has already been dispatched and does not observe its own cancellation token
/// keeps running to its own terminal state (still counted at completion); per-job cancellation
/// propagation is a later release. A gate-parked run is the clean case.</para>
/// </remarks>
public interface IBatchRunCanceller
{
    /// <summary>
    /// Requests cancellation of the run. Returns <c>true</c> if the run id was found live and signalled;
    /// <c>false</c> if it was unknown (never started, or already finished and removed). Idempotent and
    /// safe to call on an already-cancelling run.
    /// </summary>
    bool Cancel(string batchId);
}
