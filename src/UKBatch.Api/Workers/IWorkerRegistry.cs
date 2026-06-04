using UKBatch.Abstractions.Workers;

namespace UKBatch.Api.Workers;

/// <summary>
/// Live, in-memory registry of worker heartbeats. Observability ONLY — the server
/// NEVER consults this registry to make a dispatch decision (the orchestrator reaches a worker over
/// the configured <c>ITransport</c>, not via the registry). Backed by
/// <see cref="InMemoryWorkerRegistry"/>; no persistent table (single-server-instance assumption).
/// </summary>
/// <remarks>
/// Both members are SYNC — they touch only an in-memory <c>ConcurrentDictionary</c> (no IO), so they
/// take no <c>CancellationToken</c>. The caller supplies an explicit <c>now</c> so callers can pass
/// <c>TimeProvider.GetUtcNow()</c> (and tests can pass a <c>FakeTimeProvider</c> value) — the TTL math
/// is deterministic.
/// </remarks>
public interface IWorkerRegistry
{
    /// <summary>Upserts a worker's last-seen snapshot from a heartbeat. Thread-safe. An explicit
    /// <see cref="WorkerStatus.Offline"/> beat (graceful stop) still upserts (advancing
    /// <c>LastSeenUtc</c>) and marks the row offline immediately on the next <see cref="List"/>.</summary>
    void Upsert(WorkerBeatRequest beat, DateTimeOffset now);

    /// <summary>Snapshot of all known workers with the live <c>Online</c> flag computed at
    /// <paramref name="now"/>. Offline entries remain listed until the hard-evict horizon (so the
    /// dashboard shows "offline · last seen 2m ago" rather than the row vanishing). Sorted ordinal
    /// by name for stable UI ordering.</summary>
    IReadOnlyList<WorkerInfo> List(DateTimeOffset now);
}
