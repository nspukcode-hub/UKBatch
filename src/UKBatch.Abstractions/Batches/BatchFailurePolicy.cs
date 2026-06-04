namespace UKBatch.Abstractions.Batches;

/// <summary>Behaviour when an irrecoverable step failure occurs inside a batch.</summary>
public enum BatchFailurePolicy
{
    /// <summary>Fail the batch immediately and skip remaining steps.</summary>
    StopOnFailure = 0,

    /// <summary>Mark the failing step as <see cref="Models.JobStatus.Failed"/> and continue to the next step.</summary>
    ContinueOnFailure = 1,

    /// <summary>
    /// Run the <see cref="BatchDefinition.OnFailureSteps"/> compensating steps. If
    /// <see cref="BatchDefinition.OnFailureSteps"/> is empty this degrades to
    /// <see cref="StopOnFailure"/>.
    /// </summary>
    Compensate = 2,
}
