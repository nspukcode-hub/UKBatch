namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Read-only marker contract for a batch instance at runtime. Implemented by the executor;
/// consumers query state via <see cref="Storage.IJobExecutionReader"/>.
/// </summary>
public interface IBatch
{
    /// <summary>Identifier of the batch instance (one per run).</summary>
    string BatchId { get; }

    /// <summary>Identifier of the definition this instance was launched from.</summary>
    string DefinitionId { get; }

    /// <summary>Logical name (matches <see cref="BatchDefinition.Name"/>).</summary>
    string Name { get; }
}
