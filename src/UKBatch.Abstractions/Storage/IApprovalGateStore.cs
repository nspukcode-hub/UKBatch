namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Durable RECORD of approval gates: pending/approved/rejected plus who/when/why. Enables a host to
/// surface gate history after a restart. NOT a workflow-resume mechanism — see the durability
/// boundary in <see cref="PersistedApprovalGate"/> remarks and the package README.
/// </summary>
/// <remarks>
/// <para>The default in-memory implementation (<c>InMemoryApprovalGateStore</c>, shipped in
/// <c>UKBatch.Core</c>) keeps the InProcess deployment fully working with no behavior change. The EF
/// adapter replaces it so the records survive restarts.</para>
/// <para>Implementations MUST be thread-safe. <see cref="SaveAsync"/> is an idempotent UPSERT keyed on
/// <see cref="PersistedApprovalGate.ApprovalId"/>.</para>
/// </remarks>
public interface IApprovalGateStore
{
    /// <summary>Idempotent upsert of the gate record (insert on first sight, overwrite on re-save).</summary>
    Task SaveAsync(PersistedApprovalGate gate, CancellationToken cancellationToken);

    /// <summary>Returns the gate by id, or <c>null</c> if absent.</summary>
    Task<PersistedApprovalGate?> GetAsync(string approvalId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all gates still in <see cref="ApprovalRecordStatus.Pending"/>. Consumed at startup
    /// (rehydrate the in-memory dictionary) and by the dashboard's pending-approval feed.
    /// Order is implementation-defined but MUST be stable across calls for the same data.
    /// </summary>
    Task<IReadOnlyList<PersistedApprovalGate>> ListPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns all gate records (pending AND decided) for one batch RUN id, in a stable order
    /// (<see cref="PersistedApprovalGate.PendingSinceUtc"/> then
    /// <see cref="PersistedApprovalGate.ApprovalId"/>). Returns an empty list when the run has no
    /// gates. Consumed by the dashboard to colour gate nodes from their own decided outcome.
    /// </summary>
    Task<IReadOnlyList<PersistedApprovalGate>> ListByBatchAsync(string batchId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a terminal decision (approved/rejected/timed-out/cancelled) on a previously-pending gate.
    /// Throws <see cref="ApprovalGateNotFoundException"/> if the gate is absent, and
    /// <see cref="ApprovalAlreadyDecidedException"/> if the gate is already
    /// <see cref="ApprovalRecordStatus.Decided"/> — terminal outcomes are immutable, so the first
    /// recorded decision wins and a later duplicate must not overwrite it. Both derive from
    /// <see cref="InvalidOperationException"/> for 4xx mapping compatibility.
    /// </summary>
    Task RecordOutcomeAsync(
        string approvalId,
        ApprovalRecordOutcome outcome,
        string decidedBy,
        DateTimeOffset decidedAtUtc,
        string? note,
        CancellationToken cancellationToken);
}
