namespace UKBatch.Runtime;

/// <summary>
/// Reserved <c>ukbatch.*</c> keys under which a run's durable forwarded state is stored on
/// <see cref="Abstractions.Models.BatchRun.ForwardedState"/>, so the resume path can rehydrate the
/// batch-initial parameters and the accumulated step outputs after a host restart.
/// </summary>
internal static class ForwardedStateKeys
{
    /// <summary>Holds the batch-initial parameter dictionary (so a resume re-supplies the original trigger parameters).</summary>
    public const string InitialParameters = "ukbatch.initialParameters";

    /// <summary>Holds the snapshot of accumulated step outputs (so a resume forwards earlier steps' outputs).</summary>
    public const string ForwardedOutputs = "ukbatch.forwardedOutputs";
}
