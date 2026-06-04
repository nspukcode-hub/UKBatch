using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Runtime/adapter-facing extension of <see cref="IJobStore"/> for inserting an execution row whose
/// id (and all other fields, notably <see cref="JobExecution.BatchDefinitionId"/>) were assembled by
/// the runtime BEFORE the row is persisted. Application code uses <see cref="IJobStore"/> and never
/// calls this; only <c>JobRunner.TriggerInternalAsync</c> consumes it.
/// </summary>
/// <remarks>
/// <para><b>Why Abstractions-public, not a Core friend type:</b> the runtime seam in <c>JobRunner</c>
/// must dispatch polymorphically across InMemory and every future adapter (EF Core, Redis, RabbitMQ)
/// WITHOUT growing the Core <c>InternalsVisibleTo</c> friend list. The "Internal" in the name is a
/// ROLE marker (runtime/adapter contract), not a CLR accessibility modifier.</para>
/// <para><b>Field-fidelity contract:</b> implementations MUST persist <see cref="JobExecution"/>
/// verbatim, including <see cref="JobExecution.BatchDefinitionId"/>. The fallback warning in
/// <c>JobRunner</c> exists precisely because <see cref="IJobExecutionWriter.CreateAsync"/> cannot carry
/// that field; an adapter implementing this interface eliminates the fallback path.</para>
/// </remarks>
public interface IJobStoreInternal : IJobStore
{
    /// <summary>
    /// Inserts a fully-formed execution row using its pre-assigned <see cref="JobExecution.ExecutionId"/>.
    /// Throws <see cref="InvalidOperationException"/> if a row with that id already exists.
    /// </summary>
    Task<JobExecution> InsertAsync(JobExecution execution, CancellationToken cancellationToken);
}
