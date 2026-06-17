using System.Collections.Concurrent;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage;

/// <summary>
/// In-memory <see cref="IBatchRunStore"/> over a <see cref="ConcurrentDictionary{TKey,TValue}"/>. No
/// watch fan-out — run records are written once at create and once at completion and read on navigate.
/// </summary>
public sealed class InMemoryBatchRunStore : IBatchRunStore
{
    private readonly ConcurrentDictionary<string, BatchRun> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task CreateAsync(BatchRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(run.BatchId);
        if (!_runs.TryAdd(run.BatchId, run))
        {
            throw new InvalidOperationException($"Batch run {run.BatchId} already exists.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CompleteAsync(
        string batchId, JobStatus terminalStatus, BatchRunCounts counts,
        DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        // No-op when the run row is absent (the create write may have failed): a missing key must NOT
        // resurrect a half-populated row, otherwise a completion that lost its create would insert a
        // run with no StepCount/StartedAt. Compare-and-swap until the update sticks or the row vanishes.
        while (_runs.TryGetValue(batchId, out var existing))
        {
            var updated = existing with
            {
                Status = terminalStatus,
                Total = counts.Total,
                Succeeded = counts.Succeeded,
                Failed = counts.Failed,
                Cancelled = counts.Cancelled,
                CompletedAtUtc = completedAtUtc,
            };
            if (_runs.TryUpdate(batchId, updated, existing))
            {
                break;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateCursorAsync(string batchId, int nextStepIndex, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        // No-op when the run row is absent, mirroring CompleteAsync: a torn-down or never-created run
        // must not resurrect a half-row. Compare-and-swap until the update sticks or the row vanishes.
        while (_runs.TryGetValue(batchId, out var existing))
        {
            var updated = existing with { CurrentStepIndex = nextStepIndex };
            if (_runs.TryUpdate(batchId, updated, existing))
            {
                break;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        return Task.FromResult(_runs.TryGetValue(batchId, out var run) ? run : null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = ApplyFilter(_runs.Values.AsEnumerable(), query);
        snapshot = query.DescendingByStartedAt
            ? snapshot.OrderByDescending(r => r.StartedAtUtc).ThenByDescending(r => r.BatchId, StringComparer.Ordinal)
            : snapshot.OrderBy(r => r.StartedAtUtc).ThenBy(r => r.BatchId, StringComparer.Ordinal);
        var page = snapshot.Skip(Math.Max(0, query.Offset)).Take(Math.Max(0, query.Limit)).ToList();
        return Task.FromResult<IReadOnlyList<BatchRun>>(page);
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult((long)ApplyFilter(_runs.Values.AsEnumerable(), query).Count());
    }

    private static IEnumerable<BatchRun> ApplyFilter(IEnumerable<BatchRun> source, BatchRunQuery query)
    {
        if (!string.IsNullOrEmpty(query.BatchDefinitionId))
        {
            source = source.Where(r => string.Equals(r.BatchDefinitionId, query.BatchDefinitionId, StringComparison.Ordinal));
        }
        // Status filtering: a null (running) run is included iff IncludeRunning; a terminal run is
        // included iff (no status filter) OR (its status is in the set).
        var statuses = query.Statuses;
        var hasStatusFilter = statuses is { Count: > 0 };
        source = source.Where(r =>
            r.Status is null
                ? query.IncludeRunning
                : !hasStatusFilter || statuses!.Contains(r.Status.Value));
        return source;
    }
}
