using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Read-only lookup for registered <see cref="JobDefinition"/> instances. Symmetric with
/// <see cref="Batches.IBatchDefinitionLookup"/>. Implementations MUST be thread-safe and
/// lock-free for reads.
/// </summary>
/// <remarks>
/// All lookups are synchronous because job definitions are held in-process and never
/// involve I/O. The implementation backing this interface is registered as a DI singleton
/// by <c>UKBatchBuilder.Complete()</c>.
/// </remarks>
public interface IJobDefinitionLookup
{
    /// <summary>
    /// Returns the definition whose <see cref="JobDefinition.Name"/> equals
    /// <paramref name="jobName"/> (ordinal comparison), or <c>null</c> if absent.
    /// Throws <see cref="ArgumentException"/> if <paramref name="jobName"/> is null or empty.
    /// </summary>
    JobDefinition? TryGet(string jobName);

    /// <summary>
    /// Snapshot of every registered definition in REGISTRATION ORDER (the order in which
    /// <c>UKBatchBuilder.AddJob&lt;T&gt;()</c> was called during host setup). The returned list
    /// is a defensive copy.
    /// </summary>
    /// <remarks>
    /// Consumers needing alphabetical ordering MUST sort the snapshot themselves.
    /// </remarks>
    IReadOnlyList<JobDefinition> All();
}
