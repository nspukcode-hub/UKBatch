namespace UKBatch.Abstractions.Models;

/// <summary>
/// How a resumed batch run picks its starting step. Passed to the resume entry point; the
/// automatic crash-recovery path always uses <see cref="ResumeForward"/>.
/// </summary>
/// <remarks>
/// Resume NEVER auto-compensates: every mode runs forward from the chosen step. Skipping completed
/// steps relies on those steps being safe to skip (the run already did them); re-running steps relies
/// on them being safe to repeat. The library cannot know which is which, so the policy is the
/// operator's explicit choice for a manual resume, and <see cref="ResumeForward"/> is the safe
/// automatic default (do not repeat work that already finished).
/// </remarks>
public readonly record struct ResumePolicy
{
    private enum Mode { ResumeForward, RestartAll, RestartFrom }

    private readonly Mode _mode;
    private readonly int _fromIndex;

    private ResumePolicy(Mode mode, int fromIndex)
    {
        _mode = mode;
        _fromIndex = fromIndex;
    }

    /// <summary>Continue from the recorded cursor, skipping completed steps. The automatic recovery default.</summary>
    public static ResumePolicy ResumeForward { get; } = new(Mode.ResumeForward, 0);

    /// <summary>Re-run every step from the beginning (only safe when all steps are idempotent).</summary>
    public static ResumePolicy RestartAll { get; } = new(Mode.RestartAll, 0);

    /// <summary>Re-run from step <paramref name="stepIndex"/> onward (operator override after a fix).</summary>
    public static ResumePolicy RestartFrom(int stepIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stepIndex);
        return new(Mode.RestartFrom, stepIndex);
    }

    /// <summary>
    /// Resolves the executor's <c>startStepIndex</c> from the run's recorded cursor.
    /// <paramref name="cursor"/> is <see cref="BatchRun.CurrentStepIndex"/> (null when unrecorded).
    /// </summary>
    public int ResolveStartIndex(int? cursor) => _mode switch
    {
        Mode.ResumeForward => cursor ?? 0,
        Mode.RestartAll => 0,
        Mode.RestartFrom => _fromIndex,
        _ => 0,
    };
}
