using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Api.Hub;
using UKBatch.Api.Jobs;
using UKBatch.Api.Workers;

namespace UKBatch.Api;

/// <summary>
/// Endpoint-mapping helpers. Mount the UKBatch REST + hub surface under any
/// <see cref="RouteGroupBuilder"/> (typically <c>app.MapGroup("/api")</c>).
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>Maps every UKBatch.Api surface on the given group. Convenience for the common case.</summary>
    public static RouteGroupBuilder MapUKBatchApi(this RouteGroupBuilder group)
        => MapUKBatchApi(group, operationIdPrefix: null);

    /// <summary>
    /// Maps the UKBatch REST surface under <paramref name="group"/>. When
    /// <paramref name="operationIdPrefix"/> is non-null, every endpoint's <c>WithName</c> id is
    /// prefixed (e.g. <c>SecuredListBatches</c>) so the same surface can be mounted twice without
    /// OpenAPI operation-id collisions.
    /// </summary>
    /// <remarks>
    /// <para>Use the prefix overload when mounting the SAME surface under TWO groups (e.g.
    /// <c>/api</c> anonymous + <c>/api/secured</c> protected). Without the prefix, the second
    /// mount would register a second <c>WithName("ListBatches")</c> endpoint, which .NET's
    /// endpoint metadata system rejects at build time.</para>
    /// <para>The hub is NOT operation-id-prefixed (SignalR routes are independent of the OpenAPI
    /// document); the same hub path is mounted for the parameterless overload only.</para>
    /// </remarks>
    public static RouteGroupBuilder MapUKBatchApi(this RouteGroupBuilder group, string? operationIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(group);
        JobsEndpoints.Map(group, operationIdPrefix);
        BatchesEndpoints.Map(group, operationIdPrefix);
        ApprovalsEndpoints.Map(group, operationIdPrefix);
        ExecutionsEndpoints.Map(group, operationIdPrefix);
        WorkersEndpoints.Map(group, operationIdPrefix);   // /workers/* (auth-agnostic, dual-mount-safe via prefix)
        // Hub: mount ONCE only (on the parameterless / null-prefix path) — SignalR endpoints can't
        // be duplicated across mounts. The secured group inherits the hub via its parent path.
        if (operationIdPrefix is null)
        {
            group.MapHubApi();
        }
        return group;
    }

    /// <summary>
    /// Opt-in role gating for the mounted UKBatch surface. Reads each endpoint's access-kind tag and
    /// adds an authorization requirement mapping read/decision endpoints to
    /// <paramref name="readPolicy"/> and write endpoints to <paramref name="writePolicy"/>. Not
    /// calling this leaves every endpoint anonymous, exactly as the default posture.
    /// </summary>
    /// <param name="group">The mounted UKBatch route group (typically <c>app.MapGroup("/api").MapUKBatchApi()</c>).</param>
    /// <param name="readPolicy">Policy name applied to read and gate-decision endpoints (a viewer-level policy).</param>
    /// <param name="writePolicy">Policy name applied to write endpoints (an operator-level policy).</param>
    /// <remarks>
    /// <para>Approve and reject endpoints map to the read (viewer) policy, NOT the write policy: the
    /// approval gate's own allowed-roles check is the real authority, so an approver who holds a gate
    /// role but not the operator role can still act. The worker heartbeat ingest stays ungated
    /// (reached over a trusted network or gateway).</para>
    /// <para>The requirement is applied with a <c>Finally</c> convention so it runs AFTER each
    /// endpoint's own access-kind tag is in place — group conventions run before endpoint
    /// conventions, so an <c>Add</c> convention would see no tag yet and silently gate nothing. The
    /// <c>Finally</c> pass also reaches endpoints defined in nested sub-groups such as
    /// <c>/approvals</c>. It is idempotent and composes with a caller who also chains
    /// <c>RequireAuthorization</c>: authorization metadata entries combine with AND, so the chained
    /// default requirement and the role policy both apply.</para>
    /// <para>Opting into role gating means "no anonymous caller": besides the named policy, every
    /// gated endpoint also carries a plain authorization requirement (the host's default policy), so
    /// a permissive or misconfigured named policy can never re-open the surface to unauthenticated
    /// callers. Endpoints without an access-kind tag fail CLOSED to the write policy — a future
    /// endpoint that misses its tag ships operator-gated rather than silently anonymous; for the
    /// same reason, map any endpoints of your own on a separate route group.</para>
    /// <para>The policy names are the cross-package contract. A host that registers its own
    /// same-named viewer/operator policies can role-gate the surface without any authentication
    /// integration package.</para>
    /// </remarks>
    public static RouteGroupBuilder RequireUKBatchRoleAuthorization(
        this RouteGroupBuilder group,
        string readPolicy = "UKBatch:Viewer",
        string writePolicy = "UKBatch:Operator")
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(writePolicy);
        ((IEndpointConventionBuilder)group).Finally(builder =>
        {
            var kind = builder.Metadata.OfType<UKBatchEndpointAccessMetadata>().LastOrDefault()?.Kind;
            var policy = kind switch
            {
                UKBatchAccessKind.Write => writePolicy,
                UKBatchAccessKind.Read or UKBatchAccessKind.GateDecision => readPolicy,
                UKBatchAccessKind.Ingest => null, // worker ingest is deliberately ungated (trusted network / gateway)
                // Untagged fails CLOSED to the write policy: an endpoint that misses its access-kind
                // tag ships operator-gated rather than silently anonymous.
                _ => writePolicy,
            };
            if (policy is null)
            {
                return;
            }

            var authorizeData = builder.Metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().ToList();

            // The named policies are host-supplied and may lack an authenticated-user requirement (a
            // permissive fallback policy can leak in from an auth-off registration elsewhere in the
            // host). Opting into role gating means "no anonymous caller", so pin that floor with a
            // plain authorization entry (the default policy denies anonymous) unless one is already
            // present — e.g. from a chained RequireAuthorization().
            if (!authorizeData.Any(a => string.IsNullOrEmpty(a.Policy)))
            {
                builder.Metadata.Add(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute());
            }

            // Skip only when THIS policy is already attached (a second convention run). Any other
            // authorization metadata — e.g. the default entry RequireAuthorization() stamps on every
            // endpoint — must not suppress the role requirement: entries combine with AND, so adding
            // ours alongside strengthens the endpoint, while skipping would leave writes reachable by
            // any authenticated caller.
            if (!authorizeData.Any(a => string.Equals(a.Policy, policy, StringComparison.Ordinal)))
            {
                builder.Metadata.Add(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute(policy));
            }
        });
        return group;
    }

    /// <summary>Maps the Jobs surface (/jobs[...]).</summary>
    public static RouteGroupBuilder MapJobsApi(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        JobsEndpoints.Map(group);
        return group;
    }

    /// <summary>Maps the Batches surface (/batches[...]).</summary>
    public static RouteGroupBuilder MapBatchesApi(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        BatchesEndpoints.Map(group);
        return group;
    }

    /// <summary>Maps the Approvals surface (/approvals[...]).</summary>
    public static RouteGroupBuilder MapApprovalsApi(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ApprovalsEndpoints.Map(group);
        return group;
    }

    /// <summary>Maps the Executions surface (/executions[...]).</summary>
    public static RouteGroupBuilder MapExecutionsApi(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ExecutionsEndpoints.Map(group);
        return group;
    }

    /// <summary>
    /// Maps the SignalR hub at the group's effective path + <see cref="UKBatchOptions.HubPath"/>
    /// (default <c>"/hubs/jobs"</c>).
    /// </summary>
    public static RouteGroupBuilder MapHubApi(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        // RouteGroupBuilder implements IEndpointRouteBuilder; cast for ServiceProvider access.
        IEndpointRouteBuilder endpoints = group;
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<UKBatchOptions>>();
        // Tag the hub as a read surface so an opt-in role-gating convention maps it to the read
        // policy. Inert until a host opts in — live updates stay anonymous by default.
        var hub = group.MapHub<JobStatusHub>(options.Value.HubPath);
        hub.WithMetadata(new UKBatchEndpointAccessMetadata(UKBatchAccessKind.Read));
        return group;
    }
}
