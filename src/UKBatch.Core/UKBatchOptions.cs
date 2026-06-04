using System.Reflection;
using System.Security.Claims;
using Cronos;

namespace UKBatch;

/// <summary>Tunables for the UKBatch runtime; exposed via <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>.</summary>
public sealed class UKBatchOptions
{
    /// <summary>Max concurrent in-flight executions across the dispatcher. Default = <see cref="Environment.ProcessorCount"/>. Must be >= 1; validated at host start (IValidateOptions).</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Capacity of the dispatcher channel; backpressures triggers when full. Default =
    /// <c>MaxDegreeOfParallelism * 32</c>. Must be >= <c>MaxDegreeOfParallelism</c>; validated at host start.
    /// </summary>
    public int DispatcherChannelCapacity { get; set; }

    /// <summary>Default <see cref="UKBatch.Abstractions.Models.JobDefinition.MaxRetries"/> when neither attribute nor fluent sets it. Default 0.</summary>
    public int DefaultMaxRetries { get; set; }

    /// <summary>Default timeout in seconds; 0 = no timeout. Default 0.</summary>
    public int DefaultTimeoutSeconds { get; set; }

    /// <summary>Default partition worker count for <c>IPartitionedJob&lt;T&gt;</c>. Default <see cref="Environment.ProcessorCount"/>.</summary>
    public int DefaultPartitionWorkerCount { get; set; } = Environment.ProcessorCount;

    /// <summary>Max wait time on <c>StopAsync</c> for in-flight workers to drain. Default 30s. Must be >= <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Default buffer capacity for <see cref="UKBatch.Abstractions.Storage.WatchOptions"/> when omitted. Default 1024.</summary>
    public int WatchBufferCapacity { get; set; } = 1024;

    /// <summary>How often <see cref="UKBatch.Abstractions.Jobs.IJobProgress"/> deltas are flushed to the store. Default 250ms; must be &gt; <see cref="TimeSpan.Zero"/>; validated at host start.</summary>
    public TimeSpan ProgressFlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Cron grammar — <c>IncludeSeconds</c> (6-field) is the default; <c>Standard</c> (5-field) for legacy.</summary>
    public CronFormat CronFormat { get; set; } = CronFormat.IncludeSeconds;

    /// <summary>
    /// Optional list of assemblies for <see cref="Discovery.AttributeJobDiscovery"/> to scan in
    /// addition to <see cref="AppDomain.GetAssemblies"/>. Useful for plugin assemblies not yet loaded.
    /// </summary>
    /// <remarks>
    /// Read-only post-construction. Discovery happens at <c>AddUKBatch</c> time; mutations after that
    /// have no effect. Use <see cref="Builders.UKBatchBuilder.ScanAssemblies"/> or
    /// <see cref="Builders.UKBatchBuilder.Configure"/> BEFORE the registration returns.
    /// </remarks>
    public IReadOnlyList<Assembly> AdditionalAssembliesToScan { get; init; } = [];

    /// <summary>Identity recorded on <see cref="UKBatch.Abstractions.Models.JobExecution.TriggeredBy"/> for scheduler-fired runs. Default <c>"scheduler"</c>.</summary>
    public string SchedulerTriggerIdentity { get; set; } = "scheduler";

    /// <summary>
    /// Per-fan-out buffer capacity for the SignalR fan-out pump (<c>JobStatusHubFanout</c>).
    /// When the in-memory store's <c>WatchAsync</c> buffer overflows, events are dropped silently
    /// (per <c>WatchOverflowPolicy.Backpressure</c> = best-effort DropNewest in v0.1). Default 256;
    /// must be &gt;= 1.
    /// </summary>
    public int HubBufferCapacity { get; set; } = 256;

    /// <summary>
    /// Maximum page size for REST list endpoints (<c>GET /jobs</c>, <c>GET /batches</c>,
    /// <c>GET /batches/{id}/status</c>, <c>POST /executions/query</c>). Default 500; must be &gt;= 1.
    /// </summary>
    public int MaxPageLimit { get; set; } = 500;

    /// <summary>
    /// Default page size for REST list endpoints when the caller omits <c>limit</c>. Default 50;
    /// must be &gt;= 1 and &lt;= <see cref="MaxPageLimit"/>.
    /// </summary>
    public int DefaultPageLimit { get; set; } = 50;

    /// <summary>
    /// Relative URL path where <c>MapHubApi</c> mounts the SignalR hub. Default
    /// <c>"/hubs/jobs"</c>. MUST start with <c>/</c>, non-empty, no whitespace.
    /// </summary>
    public string HubPath { get; set; } = "/hubs/jobs";

    /// <summary>
    /// Upper bound on <c>JobQueryRequest.Statuses</c> array length
    /// (POST <c>/executions/query</c>). Prevents pathological clients from posting tens of
    /// thousands of entries to force the in-memory reader's <c>O(N*M)</c> linear scan. Default 20;
    /// must be &gt;= 1. Tune higher for adapter packages with indexed status columns.
    /// </summary>
    public int MaxQueryStatusesCount { get; set; } = 20;

    /// <summary>
    /// Upper bound on <c>JobQueryRequest.SearchText</c> length
    /// (POST <c>/executions/query</c>). Prevents 100KB substring searches against the
    /// in-memory reader's per-execution <c>Contains(...)</c> scan. Default 1024 chars;
    /// must be &gt;= 1.
    /// </summary>
    public int MaxQuerySearchTextLength { get; set; } = 1024;

    /// <summary>
    /// Claim types scanned when building <c>ApproverContext.Roles</c> from
    /// <c>HttpContext.User</c> in the approval endpoints (POST <c>/approvals/{id}/approve</c>,
    /// POST <c>/approvals/{id}/reject</c>). Default <c>[ClaimTypes.Role]</c> (single entry).
    /// </summary>
    /// <remarks>
    /// <para>SAML / OIDC providers commonly use non-standard claim types for roles (e.g.
    /// IdentityServer's <c>"role"</c>, Azure AD's <c>"roles"</c>, custom URIs). Configure this
    /// list to add the schemes your auth provider emits; <c>BuildApproverFromHttpContext</c> scans
    /// EVERY configured type and dedupes the role values via <see cref="StringComparer.Ordinal"/>.</para>
    /// <para><b>Validation:</b> must be non-empty (at least 1 entry); no whitespace-only
    /// entries; no duplicate entries (ordinal compare).</para>
    /// <para><b>Snapshot at call time</b> — the endpoint reads <c>options.Value.ApprovalRoleClaimTypes</c>
    /// at each Approve / Reject call; callers reconfiguring via <c>IOptionsMonitor</c> see the new
    /// value on the next request without restart.</para>
    /// <para><b>Type is <see cref="List{T}"/> not <see cref="IReadOnlyList{T}"/></b> because
    /// <c>ConfigurationBinder</c> (.NET 10) only populates <c>List&lt;T&gt;</c> / <c>T[]</c> /
    /// <c>IList&lt;T&gt;</c> / <c>ICollection&lt;T&gt;</c> shapes. With <c>IReadOnlyList&lt;string&gt;</c>,
    /// <c>appsettings.json</c> binding would silently skip → default survives → production broken.</para>
    /// </remarks>
    public List<string> ApprovalRoleClaimTypes { get; set; } = new() { ClaimTypes.Role };

    /// <summary>
    /// Service identity stamped on outbound
    /// <see cref="UKBatch.Abstractions.Transport.JobMessage.SourceService"/> for cross-service
    /// batch steps. REQUIRED if any registered batch contains a step with
    /// <c>step.Job.TargetService != null</c>; ignored otherwise.
    /// </summary>
    /// <remarks>
    /// <para><b>Resolution chain (BatchExecutor cross-service path):</b></para>
    /// <list type="number">
    ///   <item>If <c>ThisServiceName</c> is set (non-null, non-whitespace) → use it.</item>
    ///   <item>Otherwise, if env var <c>UKBATCH_SERVICE_NAME</c> is set non-empty → use it.</item>
    ///   <item>Otherwise, fall back to <c>Assembly.GetEntryAssembly()?.GetName().Name</c>.</item>
    ///   <item>If none of the above resolves to a non-empty string → fail-fast at BatchExecutor
    ///         cross-service dispatch with a clear operator-friendly
    ///         <see cref="InvalidOperationException"/> naming BOTH config paths in the message.</item>
    /// </list>
    /// <para><b>Production caveat:</b> deployments using container orchestrators (Kubernetes, ECS)
    /// often inject pod/task identity via env var. Setting <c>UKBATCH_SERVICE_NAME</c> in the
    /// deployment manifest is the idiomatic path. <c>ThisServiceName</c> in <c>appsettings.json</c>
    /// is for dev / sample scenarios.</para>
    /// <para><b>No validator at host-start.</b> The fail-fast lives at the BatchExecutor cross-service
    /// dispatch site, NOT at registration — because: (a) the runtime cannot statically inspect every
    /// registered batch for cross-service steps before host start (store-defined batches are not
    /// known at registration); (b) a node may legitimately be receiver-only (no outbound
    /// batches) in which case empty <c>ThisServiceName</c> is fine.</para>
    /// </remarks>
    public string? ThisServiceName { get; set; }
}
