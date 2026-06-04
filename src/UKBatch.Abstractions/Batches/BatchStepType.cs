namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Discriminator for <see cref="BatchStep"/>.
/// <para>
/// Numeric values are stable across versions; new step types will be appended. Consumers switching
/// on this enum MUST include a <c>default:</c> arm and deserializers MUST tolerate unknown values
/// (see <see cref="BatchStep"/> remarks).
/// </para>
/// </summary>
public enum BatchStepType
{
    /// <summary>Single job dispatch (see <see cref="JobStepData"/>).</summary>
    Job = 0,

    /// <summary>Fan-out + fan-in over child steps (see <see cref="ParallelGroupData"/>).</summary>
    ParallelGroup = 1,

    /// <summary>Manual pause until approval is granted or times out (see <see cref="ApprovalGateConfig"/>).</summary>
    ApprovalGate = 2,
}
