using UKBatch.Abstractions.Workers;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// One pickable "job @ service" entry for the batch wizard / editor Job-step dropdown. The
/// unit the dropdown offers: a <see cref="JobName"/> plus the <see cref="ServiceName"/> it lives on
/// (<c>null</c> = the local / this-service catalog from <c>IJobDefinitionLookup</c>; non-null = a remote
/// worker that advertised the job via heartbeat — <c>GET /api/workers</c>).
/// </summary>
/// <remarks>
/// <para><b>Why this exists:</b> a pure orchestrator (server + workers mode) runs no in-process jobs, so its
/// <c>GET /api/jobs</c> returns an empty list — the operator was forced to hand-type <see cref="JobName"/>
/// + the target service, and a typo silently mis-routed (the message waits forever in a quorum queue).
/// The catalog folds the worker-advertised jobs INTO the dropdown so each option carries the routing
/// target with it; selecting one sets both <c>JobName</c> and <c>TargetService</c> in one action.</para>
/// <para>Dashboard-local view model (not a wire DTO) — built best-effort as the UNION of the worker
/// snapshot and the local job lookup, deduped on <c>(JobName, ServiceName)</c> ordinal.</para>
/// </remarks>
/// <param name="JobName">The logical job name (the routing key the worker matches on <c>OnService</c>).</param>
/// <param name="ServiceName">
/// The owning service / worker name, or <c>null</c> for a local (this-service) job. A non-null value is
/// used verbatim as the step's <c>TargetService</c> routing key (a raw string — it dispatches without a
/// configured descriptor).
/// </param>
public sealed record class JobCatalogEntry(string JobName, string? ServiceName)
{
    /// <summary>
    /// Builds the dropdown catalog as the UNION of worker-advertised jobs and local jobs, deduped on
    /// <c>(JobName, ServiceName)</c> ordinal and sorted stably (JobName, then ServiceName with local
    /// — <c>null</c> — first). Each input is independent: pass whatever each best-effort call returned
    /// (an empty/failed call simply contributes nothing — the catalog still renders).
    /// </summary>
    /// <param name="workers">The <c>GET /api/workers</c> snapshot (each worker's advertised jobs).</param>
    /// <param name="localJobNames">The local <c>GET /api/jobs</c> names (embedded mode; <c>ServiceName=null</c>).</param>
    public static IReadOnlyList<JobCatalogEntry> Build(
        IReadOnlyList<WorkerInfo> workers,
        IReadOnlyList<string> localJobNames)
    {
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(localJobNames);

        // HashSet de-dupes the (JobName, ServiceName) pair across the two sources (e.g. a job advertised
        // by two beats, or a local name that also appears on a worker) before the stable sort.
        var seen = new HashSet<(string, string?)>();
        var entries = new List<JobCatalogEntry>();

        foreach (var worker in workers)
        {
            foreach (var job in worker.Jobs)
            {
                if (string.IsNullOrWhiteSpace(job)) continue;
                if (seen.Add((job, worker.Name)))
                {
                    entries.Add(new JobCatalogEntry(job, worker.Name));
                }
            }
        }

        foreach (var name in localJobNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add((name, null)))
            {
                entries.Add(new JobCatalogEntry(name, ServiceName: null));
            }
        }

        // Stable order: JobName primary, ServiceName secondary (local first via the empty-string key).
        entries.Sort(static (a, b) =>
        {
            var byJob = string.CompareOrdinal(a.JobName, b.JobName);
            return byJob != 0
                ? byJob
                : string.CompareOrdinal(a.ServiceName ?? string.Empty, b.ServiceName ?? string.Empty);
        });
        return entries;
    }

    /// <summary>
    /// Merges configured service descriptors with the distinct worker service names into the
    /// Target-service dropdown options. In server + workers mode the configured descriptors (<c>Registry.All()</c>) do
    /// NOT contain the workers, yet a <c>TargetService</c> is just a routing-key string and dispatches by
    /// raw name — so the worker names must be offered too. Returns the configured descriptors first (in
    /// registration order), then a synthetic descriptor per worker name not already configured.
    /// </summary>
    /// <param name="configured">The configured service descriptors (<c>Registry.All()</c>).</param>
    /// <param name="workers">The worker snapshot whose names become routing-key targets.</param>
    public static IReadOnlyList<UKBatchServiceDescriptor> MergeTargetServices(
        IReadOnlyList<UKBatchServiceDescriptor> configured,
        IReadOnlyList<WorkerInfo> workers)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(workers);

        var byName = new HashSet<string>(configured.Select(s => s.Name), StringComparer.Ordinal);
        var merged = new List<UKBatchServiceDescriptor>(configured);

        foreach (var worker in workers)
        {
            if (string.IsNullOrWhiteSpace(worker.Name) || !byName.Add(worker.Name)) continue;
            // Synthetic descriptor: a routing-key target only. BaseUrl is unused for dispatch (the server
            // routes by name over its transport) but the record requires it — point it at a non-routable
            // placeholder so a misuse as an HTTP base is obviously wrong, and tag the origin for the UI.
            merged.Add(new UKBatchServiceDescriptor
            {
                Name = worker.Name,
                BaseUrl = new Uri("about:blank"),
                DisplayName = worker.Name,
                Tags = ["worker"],
            });
        }

        return merged;
    }
}
