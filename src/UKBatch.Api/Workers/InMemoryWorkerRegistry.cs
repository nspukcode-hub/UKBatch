using System.Collections.Concurrent;
using UKBatch.Abstractions.Workers;

namespace UKBatch.Api.Workers;

/// <summary>
/// Live-only, in-memory, TTL'd, beat-driven implementation of <see cref="IWorkerRegistry"/>. NO EF
/// table (the registry is observability state, not durable). Touches ZERO Core internals (consumes
/// only the <c>UKBatch.Abstractions.Workers</c> DTOs + <see cref="TimeProvider"/>).
/// </summary>
/// <remarks>
/// <para><b>Thread-safety:</b> the <see cref="ConcurrentDictionary{TKey,TValue}"/> indexer write and the
/// <c>foreach</c> snapshot are individually safe. <see cref="List"/> may race with a concurrent
/// <see cref="Upsert"/> (a worker added mid-enumeration may or may not appear) — acceptable for an
/// observability snapshot. Lazy hard-evict inside <see cref="List"/> is the only mutation on read;
/// <c>TryRemove</c> is safe under enumeration.</para>
/// <para><b>TTLs are fixed constants:</b> no option is exposed, to keep the surface small.
/// A configurable TTL (<c>WorkerRegistryOptions</c>) is a future candidate.</para>
/// </remarks>
internal sealed class InMemoryWorkerRegistry : IWorkerRegistry
{
    /// <summary>Beat older than this with a last <see cref="WorkerStatus.Online"/> status is reported
    /// <c>Online=false</c> (3× the default 15s worker beat cadence — two missed beats + slack).</summary>
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(45);

    /// <summary>Any row whose last beat is older than this is hard-evicted (dropped from the list)
    /// the next time <see cref="List"/> runs, so a long-departed worker stops cluttering the panel.</summary>
    private static readonly TimeSpan HardEvictAfter = TimeSpan.FromMinutes(10);

    private sealed record class Entry(WorkerBeatRequest Beat, DateTimeOffset LastSeen);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryWorkerRegistry(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public void Upsert(WorkerBeatRequest beat, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(beat);
        // Offline beat (StopAsync) still upserts — LastSeen advances but List computes Online from
        // Status too, so an explicit Offline beat marks the row offline immediately.
        _store[beat.Name] = new Entry(beat, now);
    }

    public IReadOnlyList<WorkerInfo> List(DateTimeOffset now)
    {
        var result = new List<WorkerInfo>(_store.Count);
        foreach (var (name, entry) in _store)
        {
            var age = now - entry.LastSeen;
            if (age > HardEvictAfter)
            {
                _store.TryRemove(name, out _);   // lazy hard-evict
                continue;
            }

            var online = entry.Beat.Status == WorkerStatus.Online && age <= OnlineTtl;
            result.Add(new WorkerInfo
            {
                Name = name,
                Jobs = entry.Beat.Jobs,
                Tags = entry.Beat.Tags,
                Status = entry.Beat.Status,
                LastSeenUtc = entry.LastSeen,
                Online = online,
            });
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));   // stable UI ordering
        return result;
    }
}
