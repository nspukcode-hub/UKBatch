using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Jobs;
using UKBatch.Registry;

namespace UKBatch.Runtime;

/// <summary>
/// Cron scheduler.
/// <list type="bullet">
///   <item>Min-heap of <see cref="ScheduledJobEntry"/> sorted by next fire deadline.</item>
///   <item>Wake signal is a <see cref="Channel{T}"/> bounded to 1 with <see cref="BoundedChannelFullMode.DropOldest"/>
///         so registry-change notifications collapse safely under contention.</item>
///   <item>Loop awaits <see cref="Task.WhenAny(System.Threading.Tasks.Task[])"/> over the wake channel and
///         <see cref="Task.Delay(TimeSpan, CancellationToken)"/> until the next deadline.</item>
/// </list>
/// </summary>
/// <remarks>
/// Missed-fires-on-downtime are by-design in v0.1. For SLA-bound jobs,
/// adopt the durable scheduler planned for v0.2.
/// </remarks>
internal sealed class JobScheduler
{
    private readonly PriorityQueue<ScheduledJobEntry, DateTimeOffset> _heap = new();
#if NET10_0_OR_GREATER
    private readonly Lock _heapLock = new();
#else
    // System.Threading.Lock requires net9+; a plain monitor object is the net8.0 equivalent.
    private readonly object _heapLock = new();
#endif
    private readonly Channel<bool> _wakeChannel;
    private readonly JobDefinitionRegistry _registry;
    private readonly JobDispatcher _dispatcher;
    private readonly Abstractions.Storage.IJobStore _store;
    private readonly CronExpressionCache _cronCache;
    private readonly TimeProvider _clock;
    private readonly UKBatchOptions _options;
    private readonly ILogger<JobScheduler> _logger;

    private CancellationTokenSource? _stoppingCts;
    private Task? _loopTask;

    /// <summary>Constructs the scheduler with composed dependencies.</summary>
    public JobScheduler(
        JobDefinitionRegistry registry,
        JobDispatcher dispatcher,
        Abstractions.Storage.IJobStore store,
        CronExpressionCache cronCache,
        TimeProvider clock,
        IOptions<UKBatchOptions> options,
        ILogger<JobScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cronCache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _dispatcher = dispatcher;
        _store = store;
        _cronCache = cronCache;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _wakeChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Loads the registry snapshot and launches the loop.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nowUtc = _clock.GetUtcNow();
        lock (_heapLock)
        {
            foreach (var def in _registry.All())
            {
                if (string.IsNullOrEmpty(def.Schedule))
                {
                    continue;
                }
                var expr = _cronCache.Get(def.Schedule, _options.CronFormat);
                var next = expr.GetNextOccurrence(nowUtc.UtcDateTime, TimeZoneInfo.Utc);
                if (next is null)
                {
                    continue;
                }
                var entry = new ScheduledJobEntry
                {
                    Definition = def,
                    CronExpression = expr,
                    NextFireUtc = new DateTimeOffset(next.Value, TimeSpan.Zero),
                };
                _heap.Enqueue(entry, entry.NextFireUtc);
            }
        }
        _loopTask = Task.Run(() => LoopAsync(_stoppingCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset? nextDeadline;
            lock (_heapLock)
            {
                nextDeadline = _heap.TryPeek(out _, out var when) ? when : null;
            }

            if (nextDeadline is null)
            {
                try
                {
                    await _wakeChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                while (_wakeChannel.Reader.TryRead(out _))
                {
                    // drain backlog
                }
                continue;
            }

            var remaining = nextDeadline.Value - _clock.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var wakeTask = _wakeChannel.Reader.WaitToReadAsync(deadlineCts.Token).AsTask();
                var delayTask = Task.Delay(remaining, deadlineCts.Token);
                Task winner;
                try
                {
                    winner = await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                deadlineCts.Cancel();
                try
                {
                    await winner.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected when the loser was cancelled
                }
                if (winner == wakeTask)
                {
                    while (_wakeChannel.Reader.TryRead(out _))
                    {
                        // drain
                    }
                    continue;
                }
            }

            ScheduledJobEntry? entry;
            lock (_heapLock)
            {
                _heap.TryDequeue(out entry, out _);
            }
            if (entry is null)
            {
                continue;
            }
            await FireAsync(entry, ct).ConfigureAwait(false);

            var nextOccurrence = entry.CronExpression.GetNextOccurrence(_clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Utc);
            if (nextOccurrence is not null)
            {
                var rescheduled = entry with { NextFireUtc = new DateTimeOffset(nextOccurrence.Value, TimeSpan.Zero) };
                lock (_heapLock)
                {
                    _heap.Enqueue(rescheduled, rescheduled.NextFireUtc);
                }
            }
        }
    }

    private async Task FireAsync(ScheduledJobEntry entry, CancellationToken ct)
    {
        try
        {
            var execution = await _store.CreateAsync(entry.Definition, ct).ConfigureAwait(false);
            var request = new JobExecutionRequest
            {
                ExecutionId = execution.ExecutionId,
                Definition = entry.Definition,
                // Trusted callsite: DefaultParameters is defensive-copied at registration, so
                // WrapWithoutCopy is safe here.
                Parameters = JobParameters.WrapWithoutCopy(entry.Definition.DefaultParameters),
                AttemptNumber = 1,
                TriggeredBy = _options.SchedulerTriggerIdentity,
                BatchId = null,
                BatchStepId = null,
                EnqueuedAtUtc = _clock.GetUtcNow(),
            };
            await _dispatcher.EnqueueAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown — drop silently
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduler fire failed for {Job}", entry.Definition.Name);
        }
    }

    /// <summary>Signals the loop to exit. Idempotent.</summary>
    public Task StopAsync()
    {
        _stoppingCts?.Cancel();
        return _loopTask ?? Task.CompletedTask;
    }

    /// <summary>
    /// Hook for future dynamic registry add/remove. Multiple notifications collapse to one wake
    /// thanks to <c>Channel&lt;bool&gt;(1, DropOldest)</c>.
    /// </summary>
    public void NotifyRegistryChanged() => _wakeChannel.Writer.TryWrite(true);
}
