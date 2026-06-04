using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Core-internal thin delegator over the Abstractions-public <see cref="JobStatusTransitions"/> matrix.
/// The transition matrix DATA lives in <see cref="JobStatusTransitions"/> (the single source of truth
/// shared by every store adapter); this type adds ONLY the richer
/// <see cref="InvalidJobTransitionException"/> wrapper that Core callers' existing tests depend on.
/// </summary>
/// <remarks>
/// The matrix is Abstractions-public so the EF Core adapter (and future Redis/RabbitMQ adapters) can
/// validate transitions WITHOUT friend access to Core. Core callers
/// (<c>InMemoryJobStore</c>, <c>BatchExecutor</c>, etc.) keep calling <c>BatchStateMachine.Validate</c>
/// and still get <see cref="InvalidJobTransitionException"/> (a subtype of
/// <see cref="InvalidOperationException"/>). The EF store calls <see cref="JobStatusTransitions.Validate"/>
/// directly and gets the base <see cref="InvalidOperationException"/> that the frozen
/// <c>IJobExecutionWriter.UpdateStatusAsync</c> contract promises.
/// </remarks>
internal static class BatchStateMachine
{
    /// <summary>O(1) lookup: returns <c>true</c> if <paramref name="from"/> may transition to <paramref name="to"/>.</summary>
    public static bool CanTransition(JobStatus from, JobStatus to) => JobStatusTransitions.CanTransition(from, to);

    /// <summary>
    /// Throws <see cref="InvalidJobTransitionException"/> if the transition is illegal; otherwise returns silently.
    /// Called by every Core <c>IJobExecutionWriter.UpdateStatusAsync</c> implementation before mutating storage.
    /// </summary>
    public static void Validate(JobStatus from, JobStatus to)
    {
        if (!JobStatusTransitions.CanTransition(from, to))
        {
            throw new InvalidJobTransitionException(from, to);
        }
    }

    /// <summary>True iff <paramref name="status"/> is a terminal lifecycle state.</summary>
    public static bool IsTerminal(JobStatus status) => JobStatusTransitions.IsTerminal(status);
}
