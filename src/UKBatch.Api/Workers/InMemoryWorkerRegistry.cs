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
/// observability snapshot. The expired-entry sweep mutates the store under enumeration via
/// <c>TryRemove</c>, which is safe.</para>
/// <para><b>Bounded memory:</b> the registry holds at most <see cref="MaxWorkers"/> distinct workers.
/// Both <see cref="Upsert"/> and <see cref="List"/> first drop entries older than the hard-evict
/// horizon, so a departed worker frees its slot without anyone reading the list. If, after that sweep,
/// the registry is still full and a beat arrives from a brand-new worker name, the single entry with
/// the oldest <c>LastSeen</c> is evicted to make room. A worker that keeps beating refreshes its own
/// entry and is never evicted by this path, so legitimate fleets are unaffected; the cap only bites
/// under a flood of attacker-chosen distinct names. The cap is an internal constant, not an option,
/// to keep the surface small.</para>
/// <para><b>TTLs are fixed constants:</b> no option is exposed, to keep the surface small.
/// A configurable TTL (<c>WorkerRegistryOptions</c>) is a future candidate.</para>
/// </remarks>
internal sealed class InMemoryWorkerRegistry : IWorkerRegistry
{
    /// <summary>Maximum number of distinct workers retained. A beat from a new worker name beyond this
    /// cap evicts the oldest-seen entry, so memory stays bounded even under a flood of distinct names.</summary>
    internal const int MaxWorkers = 1000;

    /// <summary>Beat older than this with a last <see cref="WorkerStatus.Online"/> status is reported
    /// <c>Online=false</c> (3× the default 15s worker beat cadence — two missed beats + slack).</summary>
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(45);

    /// <summary>Any row whose last beat is older than this is hard-evicted (dropped from the registry).
    /// Both <see cref="Upsert"/> and <see cref="List"/> run this sweep, so a long-departed worker stops
    /// cluttering the panel and stops occupying a registry slot.</summary>
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

        // Refreshing an existing worker only advances its LastSeen — never grows the store and never
        // triggers eviction, so an active fleet is never throttled by the cap. The sweep + cap logic
        // below only runs for a beat that would add a NEW key.
        if (_store.ContainsKey(beat.Name))
        {
            // Offline beat (StopAsync) still upserts — LastSeen advances but List computes Online from
            // Status too, so an explicit Offline beat marks the row offline immediately.
            _store[beat.Name] = new Entry(beat, now);
            return;
        }

        // A genuinely new worker: reclaim expired slots first so the cap reflects only live workers.
        // Gated to "near or at capacity" so the common case (a small fleet, plenty of headroom) skips
        // the O(n) scan entirely. The race between this check and the eviction below is benign — at
        // worst we briefly hold one extra entry, which the next sweep reclaims.
        if (_store.Count >= MaxWorkers / 2)
        {
            SweepExpired(now);
        }

        // Still full after the sweep → drop the single oldest-seen entry to make room. Under a flood
        // of distinct names this keeps the store pinned at the cap instead of growing without bound.
        while (_store.Count >= MaxWorkers)
        {
            if (!TryEvictOldest())
            {
                break; // store emptied out from under us (all entries removed concurrently)
            }
        }

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
                _store.TryRemove(name, out _);   // hard-evict departed workers
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

    /// <summary>Drops every entry whose last beat is older than the hard-evict horizon. Safe to call
    /// from both the write and the read path; <c>TryRemove</c> during enumeration is well-defined.</summary>
    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var (name, entry) in _store)
        {
            if (now - entry.LastSeen > HardEvictAfter)
            {
                _store.TryRemove(name, out _);
            }
        }
    }

    /// <summary>Removes the single entry with the oldest <c>LastSeen</c>. Returns <c>false</c> only if
    /// the store is empty. The snapshot-then-remove is intentionally optimistic under concurrency:
    /// callers retry while still at cap, so a lost race just removes a different (also-old) entry.</summary>
    private bool TryEvictOldest()
    {
        string? oldestName = null;
        var oldestSeen = DateTimeOffset.MaxValue;
        foreach (var (name, entry) in _store)
        {
            if (entry.LastSeen < oldestSeen)
            {
                oldestSeen = entry.LastSeen;
                oldestName = name;
            }
        }

        return oldestName is not null && _store.TryRemove(oldestName, out _);
    }
}
