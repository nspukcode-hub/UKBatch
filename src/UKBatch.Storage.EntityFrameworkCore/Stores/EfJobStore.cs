using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Entities;
using UKBatch.Storage.EntityFrameworkCore.Mapping;

namespace UKBatch.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// EF Core implementation of <see cref="IJobStoreInternal"/> (= <see cref="IJobExecutionReader"/> +
/// <see cref="IJobExecutionWriter"/> + <c>InsertAsync</c>). One short-lived pooled context per public
/// method; watch fan-out delegates to the shared <see cref="IJobExecutionWatchHub"/> and
/// publishes AFTER the DB commit.
/// </summary>
/// <remarks>
/// <b>Singleton + factory:</b> this store is a DI SINGLETON. It MUST NOT capture a scoped
/// <c>DbContext</c> (captive dependency; <c>DbContext</c> is not thread-safe). It injects the
/// thread-safe <c>IDbContextFactory&lt;UKBatchDbContext&gt;</c> and opens
/// <c>await using var db = await _factory.CreateDbContextAsync(ct)</c> per method.
/// <para><b>Shutdown:</b> writes during host shutdown are best-effort — the factory may throw
/// <see cref="ObjectDisposedException"/> if the root provider disposes mid-flight; this store has no
/// dispose guard and relies on the factory (NTH backlog).</para>
/// </remarks>
internal sealed class EfJobStore : IJobStoreInternal
{
    private readonly IDbContextFactory<UKBatchDbContext> _factory;
    private readonly IJobExecutionWatchHub _watchHub;
    private readonly TimeProvider _clock;
    private readonly ILogger<EfJobStore> _logger;

    public EfJobStore(
        IDbContextFactory<UKBatchDbContext> factory,
        IJobExecutionWatchHub watchHub,
        TimeProvider clock,
        ILogger<EfJobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(watchHub);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _watchHub = watchHub;
        _clock = clock;
        _logger = logger;
    }

    // ===== IJobExecutionWriter =====

    /// <inheritdoc/>
    public async Task<JobExecution> CreateAsync(JobDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var now = _clock.GetUtcNow();
        var model = new JobExecution
        {
            ExecutionId = NewExecutionId(),
            JobName = definition.Name,
            BatchId = null,
            BatchStepId = null,
            BatchDefinitionId = null,
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

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.JobExecutions.Add(JobExecutionMapper.ToEntity(model));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);   // COMMIT
        _watchHub.Publish(model);                                             // AFTER commit
        return model;
    }

    /// <inheritdoc/>
    public async Task<JobExecution> InsertAsync(JobExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.JobExecutions.Add(JobExecutionMapper.ToEntity(execution));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
        {
            // Parity with InMemory's message so the fallback-path contract carries to the adapter suite.
            throw new InvalidOperationException($"Execution {execution.ExecutionId} already exists.", ex);
        }
        _watchHub.Publish(execution);   // AFTER commit
        return execution;
    }

    /// <inheritdoc/>
    public async Task UpdateStatusAsync(string executionId, JobStatus status, string? errorMessage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        var now = _clock.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var entity = await db.JobExecutions
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken).ConfigureAwait(false);   // TRACKED
        if (entity is null)
        {
            throw new KeyNotFoundException($"Execution {executionId} not found");   // parity with InMemory
        }

        // Mirror InMemoryJobStore exactly. JobStatusTransitions.Validate throws the base
        // InvalidOperationException the frozen IJobExecutionWriter contract promises (the
        // adapter does NOT depend on Core's internal exception subtype). Last-write-wins on the
        // status columns (no per-row token): a race produces only a benign reordering of two
        // LEGAL writes; an illegal loser is rejected here against the committed status.
        JobStatusTransitions.Validate(entity.Status, status);
        if (status == JobStatus.Running && entity.StartedAtUtc is null)
        {
            entity.StartedAtUtc = now;
        }
        if (JobStatusTransitions.IsTerminal(status))
        {
            entity.CompletedAtUtc = now;
        }
        entity.LastError = errorMessage ?? entity.LastError;
        entity.Status = status;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);   // COMMIT
        _watchHub.Publish(JobExecutionMapper.ToModel(entity));                // AFTER commit
    }

    /// <inheritdoc/>
    public async Task RecordAttemptAsync(string executionId, int attemptNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.JobExecutions
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            throw new KeyNotFoundException($"Execution {executionId} not found");
        }
        entity.AttemptNumber = attemptNumber;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _watchHub.Publish(JobExecutionMapper.ToModel(entity));
    }

    /// <inheritdoc/>
    public async Task UpdateProgressAsync(string executionId, long processed, long failed, long? total, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.JobExecutions
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            throw new KeyNotFoundException($"Execution {executionId} not found");
        }
        entity.Processed = processed;
        entity.Failed = failed;
        entity.Total = total;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _watchHub.Publish(JobExecutionMapper.ToModel(entity));
    }

    // ===== IJobExecutionReader =====

    /// <inheritdoc/>
    public async Task<JobExecution?> GetAsync(string executionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.JobExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : JobExecutionMapper.ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobExecution>> QueryAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<JobExecutionEntity> q = db.JobExecutions.AsNoTracking();
        q = ApplyFilter(q, query, db.Database.IsNpgsql());
        // Stable sort: EnqueuedAt then ExecutionId (mirrors InMemory).
        q = query.DescendingByEnqueuedAt
            ? q.OrderByDescending(e => e.EnqueuedAtUtc).ThenBy(e => e.ExecutionId)
            : q.OrderBy(e => e.EnqueuedAtUtc).ThenBy(e => e.ExecutionId);
        var page = await q
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Max(0, query.Limit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return page.Select(JobExecutionMapper.ToModel).ToList();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<JobExecutionEntity> q = db.JobExecutions.AsNoTracking();
        q = ApplyFilter(q, query, db.Database.IsNpgsql());   // offset/limit ignored (Reader contract)
        return await q.LongCountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<JobExecution> WatchAsync(WatchOptions options, CancellationToken cancellationToken)
        => _watchHub.WatchAsync(options, cancellationToken);

    private static IQueryable<JobExecutionEntity> ApplyFilter(IQueryable<JobExecutionEntity> q, JobQuery query, bool isNpgsql)
    {
        if (query.Statuses is { Count: > 0 } statuses)
        {
            q = q.Where(e => statuses.Contains(e.Status));
        }
        if (!string.IsNullOrEmpty(query.JobName))
        {
            q = q.Where(e => e.JobName == query.JobName);
        }
        if (!string.IsNullOrEmpty(query.BatchId))
        {
            q = q.Where(e => e.BatchId == query.BatchId);
        }
        if (!string.IsNullOrEmpty(query.BatchDefinitionId))
        {
            q = q.Where(e => e.BatchDefinitionId == query.BatchDefinitionId);
        }
        if (query.FromUtc is { } from)
        {
            q = q.Where(e => e.EnqueuedAtUtc >= from);
        }
        if (query.ToUtc is { } to)
        {
            q = q.Where(e => e.EnqueuedAtUtc < to);   // exclusive upper (JobQuery contract)
        }
        if (!string.IsNullOrEmpty(query.WorkerName))
        {
            q = q.Where(e => e.WorkerName == query.WorkerName);
        }
        if (!string.IsNullOrEmpty(query.SearchText))
        {
            // Case-insensitive substring on LastError OR JobName (mirrors InMemory OrdinalIgnoreCase).
            // PG: ILIKE (case-insensitive). SQLite: LIKE (ASCII case-insensitive by default). Escape
            // %/_ so literal wildcards in user input match literally. The escape char clause is
            // declared via the 3-arg EF.Functions overload.
            var pattern = $"%{LikeEscaper.Escape(query.SearchText)}%";
            var esc = LikeEscaper.EscapeChar.ToString();
            q = isNpgsql
                ? q.Where(e => (e.LastError != null && EF.Functions.ILike(e.LastError, pattern, esc))
                               || EF.Functions.ILike(e.JobName, pattern, esc))
                : q.Where(e => (e.LastError != null && EF.Functions.Like(e.LastError, pattern, esc))
                               || EF.Functions.Like(e.JobName, pattern, esc));
        }
        return q;
    }

    /// <summary>
    /// UUIDv7 execution id, "N" format: inlined rather than promoting Core's
    /// internal <c>IdGenerator</c> — it is an implementation utility, not a cross-store contract, so
    /// inlining adds zero public surface and zero friend growth.
    /// </summary>
    private static string NewExecutionId() => Guid.CreateVersion7().ToString("N");
}
