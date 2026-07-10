namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Derives the correlation StepId of a compensator from its parent step's id. The compensator runs as a real
/// execution with its own row; stamping that row with the parent id plus a stable suffix lets the dashboard
/// map the compensator to its parent, and lets crash-recovery prove a compensator already finished. The
/// suffix is a fixed contract: a persisted id must resolve identically across releases.
/// </summary>
public static class CompensationStepIds
{
    /// <summary>Suffix appended to a parent step id to form its compensator's correlation id. Fixed contract.</summary>
    public const string Suffix = ":comp";

    /// <summary>Returns the compensator correlation id for <paramref name="parentStepId"/>.</summary>
    public static string For(string parentStepId)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentStepId);
        return parentStepId + Suffix;
    }
}
