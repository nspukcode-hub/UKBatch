using System.Collections.Concurrent;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage;

/// <summary>
/// In-memory <see cref="IApprovalGateStore"/> — the default that ships in <c>UKBatch.Core</c> so the
/// InProcess deployment keeps approval gates working with NO behaviour change and NO durability. The EF
/// Core adapter replaces this registration so gate records survive host restarts.
/// </summary>
/// <remarks>
/// <para>Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed on
/// <see cref="PersistedApprovalGate.ApprovalId"/>. <see cref="SaveAsync"/> is an idempotent upsert
/// (insert on first sight, overwrite on re-save) — gate records are single-writer per lifecycle event
/// (create → one decision), so no concurrency token is needed.</para>
/// <para>Records here are LOST on process exit (in-memory) — that is the whole point of the EF adapter.
/// The durability boundary is durable RECORD/history (EF), not durable RESUME; in InProcess there
/// is nothing to recover because the in-memory awaiter dies with the process anyway.</para>
/// </remarks>
public sealed class InMemoryApprovalGateStore : IApprovalGateStore
{
    private readonly ConcurrentDictionary<string, PersistedApprovalGate> _gates = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task SaveAsync(PersistedApprovalGate gate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gates[gate.ApprovalId] = gate;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<PersistedApprovalGate?> GetAsync(string approvalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        return Task.FromResult(_gates.TryGetValue(approvalId, out var gate) ? gate : null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PersistedApprovalGate>> ListPendingAsync(CancellationToken cancellationToken)
    {
        // Stable order: PendingSinceUtc then ApprovalId (mirrors the EF store's ordering).
        var pending = _gates.Values
            .Where(g => g.Status == ApprovalRecordStatus.Pending)
            .OrderBy(g => g.PendingSinceUtc)
            .ThenBy(g => g.ApprovalId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<PersistedApprovalGate>>(pending);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PersistedApprovalGate>> ListByBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        // Pending AND decided for this run; stable order (mirrors the EF store's ordering).
        var gates = _gates.Values
            .Where(g => string.Equals(g.BatchId, batchId, StringComparison.Ordinal))
            .OrderBy(g => g.PendingSinceUtc)
            .ThenBy(g => g.ApprovalId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<PersistedApprovalGate>>(gates);
    }

    /// <inheritdoc/>
    public Task RecordOutcomeAsync(
        string approvalId,
        ApprovalRecordOutcome outcome,
        string decidedBy,
        DateTimeOffset decidedAtUtc,
        string? note,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentNullException.ThrowIfNull(decidedBy);

        // AddOrUpdate-style guard: throw on absent (parity with the EF store's direct-caller contract).
        if (!_gates.TryGetValue(approvalId, out var existing))
        {
            throw new InvalidOperationException($"Approval gate {approvalId} not found.");
        }
        if (existing.Status == ApprovalRecordStatus.Decided)
        {
            // Terminal outcomes are immutable — a second decision (duplicate approve, or operator vs reaper
            // race) must not overwrite the audit record. The first writer wins.
            throw new ApprovalAlreadyDecidedException(
                $"Approval gate {approvalId} is already decided ({existing.Outcome}); cannot overwrite.")
            {
                ApprovalId = approvalId,
                ExistingOutcome = existing.Outcome,
            };
        }

        _gates[approvalId] = existing with
        {
            Status = ApprovalRecordStatus.Decided,
            Outcome = outcome,
            DecidedBy = decidedBy,
            DecidedAtUtc = decidedAtUtc,
            Note = note,
        };
        return Task.CompletedTask;
    }
}
