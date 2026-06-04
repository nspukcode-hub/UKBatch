using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Internal;
using UKBatch.Runtime;

namespace UKBatch.Storage;

/// <summary>
/// In-memory <see cref="IJobStoreInternal"/>. Uses <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, Func{TKey, TValue}, Func{TKey, TValue, TValue})"/>
/// for every mutation (no CAS spin loops; deterministic worst-case).
/// </summary>
/// <remarks>
/// Watch fan-out is delegated to the shared <see cref="JobExecutionWatchHub"/> so the EF Core adapter
/// composes the SAME in-process fan-out. Behaviour is byte-for-byte identical to a per-subscriber path —
/// the subscription internals are unchanged, just one indirection away. Implements
/// <see cref="IJobStoreInternal"/> (the runtime seam at <c>JobRunner</c> dispatches polymorphically
/// across this and every future adapter).
/// </remarks>
public sealed class InMemoryJobStore : IJobStoreInternal
{
    private readonly ConcurrentDictionary<string, JobExecution> _executions = new(StringComparer.Ordinal);
    private readonly IJobExecutionWatchHub _watchHub;
    private readonly TimeProvider _clock;
    private readonly UKBatchOptions _options;

    /// <summary>Constructs the store with the injected clock, options, and shared watch hub.</summary>
    public InMemoryJobStore(TimeProvider clock, IOptions<UKBatchOptions> options, IJobExecutionWatchHub watchHub)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(watchHub);
        _clock = clock;
        _options = options.Value;
        _watchHub = watchHub;
    }

    /// <inheritdoc/>
    public Task<JobExecution> CreateAsync(JobDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var now = _clock.GetUtcNow();
        var execution = new JobExecution
        {
            ExecutionId = IdGenerator.NewExecutionId(),
            JobName = definition.Name,
            BatchId = null,
            BatchStepId = null,
            Status = JobStatus.Pending,
            Parameters = definition.DefaultParameters,
            EnqueuedAtUtc = now,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            AttemptNumber = 1,
            MaxRetries = definition.MaxRetries,
            LastError = null,
            Processed = 0,
            Failed = 0,
            Total = null,
            TriggeredBy = null,
            WorkerName = null,
        };
        if (!_executions.TryAdd(execution.ExecutionId, execution))
        {
            // UUIDv7 collision is astronomically unlikely but handle defensively.
            throw new InvalidOperationException($"Execution id {execution.ExecutionId} collided on insert.");
        }
        _watchHub.Publish(execution);
        return Task.FromResult(execution);
    }

    /// <summary>
    /// Inserts a fully-formed execution row using its pre-assigned <see cref="JobExecution.ExecutionId"/>
    /// (e.g. by <c>JobRunner</c> when the caller pre-allocated an execution id and assembled all fields,
    /// notably <see cref="JobExecution.BatchDefinitionId"/>). Part of the
    /// <see cref="IJobStoreInternal"/> contract — application code uses <see cref="IJobStore"/>.
    /// Throws <see cref="InvalidOperationException"/> if a row with that id already exists.
    /// </summary>
    public Task<JobExecution> InsertAsync(JobExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!_executions.TryAdd(execution.ExecutionId, execution))
        {
            throw new InvalidOperationException($"Execution {execution.ExecutionId} already exists.");
        }
        _watchHub.Publish(execution);
        return Task.FromResult(execution);
    }

    /// <inheritdoc/>
    public Task UpdateStatusAsync(string executionId, JobStatus status, string? errorMessage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        var now = _clock.GetUtcNow();
        var updated = _executions.AddOrUpdate(
            executionId,
            static id => throw new KeyNotFoundException($"Execution {id} not found"),
            (_, existing) =>
            {
                BatchStateMachine.Validate(existing.Status, status);
                return existing with
                {
                    Status = status,
                    LastError = errorMessage ?? existing.LastError,
                    StartedAtUtc = status == JobStatus.Running && existing.StartedAtUtc is null
                        ? now
                        : existing.StartedAtUtc,
                    CompletedAtUtc = BatchStateMachine.IsTerminal(status) ? now : existing.CompletedAtUtc,
                };
            });
        _watchHub.Publish(updated);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RecordAttemptAsync(string executionId, int attemptNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        var updated = _executions.AddOrUpdate(
            executionId,
            static id => throw new KeyNotFoundException($"Execution {id} not found"),
            (_, existing) => existing with { AttemptNumber = attemptNumber });
        _watchHub.Publish(updated);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateProgressAsync(string executionId, long processed, long failed, long? total, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        var updated = _executions.AddOrUpdate(
            executionId,
            static id => throw new KeyNotFoundException($"Execution {id} not found"),
            (_, existing) => existing with
            {
                Processed = processed,
                Failed = failed,
                Total = total,
            });
        _watchHub.Publish(updated);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<JobExecution?> GetAsync(string executionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        return Task.FromResult(_executions.TryGetValue(executionId, out var execution) ? execution : null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobExecution>> QueryAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = _executions.Values.AsEnumerable();
        snapshot = ApplyFilter(snapshot, query);
        // Stable sort: EnqueuedAt then ExecutionId.
        snapshot = query.DescendingByEnqueuedAt
            ? snapshot.OrderByDescending(e => e.EnqueuedAtUtc).ThenBy(e => e.ExecutionId, StringComparer.Ordinal)
            : snapshot.OrderBy(e => e.EnqueuedAtUtc).ThenBy(e => e.ExecutionId, StringComparer.Ordinal);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Max(0, query.Limit);
        var page = snapshot.Skip(offset).Take(limit).ToList();
        return Task.FromResult<IReadOnlyList<JobExecution>>(page);
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = _executions.Values.AsEnumerable();
        snapshot = ApplyFilter(snapshot, query);
        return Task.FromResult((long)snapshot.Count());
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<JobExecution> WatchAsync(WatchOptions options, CancellationToken cancellationToken)
        => _watchHub.WatchAsync(options, cancellationToken);

    private static IEnumerable<JobExecution> ApplyFilter(IEnumerable<JobExecution> source, JobQuery query)
    {
        if (query.Statuses is { Count: > 0 } statuses)
        {
            source = source.Where(e => statuses.Contains(e.Status));
        }
        if (!string.IsNullOrEmpty(query.JobName))
        {
            source = source.Where(e => string.Equals(e.JobName, query.JobName, StringComparison.Ordinal));
        }
        if (!string.IsNullOrEmpty(query.BatchId))
        {
            source = source.Where(e => string.Equals(e.BatchId, query.BatchId, StringComparison.Ordinal));
        }
        if (!string.IsNullOrEmpty(query.BatchDefinitionId))
        {
            // Filter on the DEFINITION id (not the RUN id). Ordinal compare matches existing string
            // filters; the v0.2 SQL adapter implements this as an indexed lookup.
            source = source.Where(e => string.Equals(e.BatchDefinitionId, query.BatchDefinitionId, StringComparison.Ordinal));
        }
        if (query.FromUtc is { } from)
        {
            source = source.Where(e => e.EnqueuedAtUtc >= from);
        }
        if (query.ToUtc is { } to)
        {
            source = source.Where(e => e.EnqueuedAtUtc < to);
        }
        if (!string.IsNullOrEmpty(query.WorkerName))
        {
            source = source.Where(e => string.Equals(e.WorkerName, query.WorkerName, StringComparison.Ordinal));
        }
        if (!string.IsNullOrEmpty(query.SearchText))
        {
            var needle = query.SearchText;
            source = source.Where(e =>
                (e.LastError is not null && e.LastError.Contains(needle, StringComparison.OrdinalIgnoreCase))
                || e.JobName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        return source;
    }
}
