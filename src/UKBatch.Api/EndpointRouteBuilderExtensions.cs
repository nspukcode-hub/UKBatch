using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
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
        group.MapHub<JobStatusHub>(options.Value.HubPath);
        return group;
    }
}
