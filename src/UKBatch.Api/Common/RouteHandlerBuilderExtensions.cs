using Microsoft.AspNetCore.Builder;

namespace UKBatch.Api.Common;

/// <summary>
/// Api-internal helpers chained onto the endpoint <c>Map</c> methods: an OpenAPI operation-id
/// prefix (so the same surface can be mounted twice, e.g. <c>/api</c> + <c>/api/secured</c>,
/// without operation-id collisions) and an access-kind tag used by role-gating.
/// </summary>
/// <remarks>
/// <para><c>internal static</c> — this is an Api-internal mounting concern,
/// NOT a public extension surface. The op-id-aware tests
/// (<c>ApiPackageInvariantsTests</c>, <c>Hub_NoHubBackpressureWarningMethod_v01</c>,
/// <c>OpenApi_NoHubBackpressureWarningInClientMethods</c>) continue to assert on bare names
/// when the parameterless <c>MapUKBatchApi()</c> mount is used.</para>
/// </remarks>
internal static class RouteHandlerBuilderExtensions
{
    /// <summary>
    /// Prefixes <paramref name="name"/> with <paramref name="prefix"/> when non-null; otherwise
    /// preserves the bare <paramref name="name"/> (the single-mount behavior).
    /// </summary>
    public static RouteHandlerBuilder WithUKBatchName(this RouteHandlerBuilder builder, string? prefix, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return builder.WithName(prefix is null ? name : prefix + name);
    }

    /// <summary>
    /// Tags the endpoint with its <see cref="UKBatchAccessKind"/> so an opt-in role-gating
    /// convention can map it to a policy. The tag is inert on its own — nothing reads it unless the
    /// host opts in — so adding it leaves default behavior byte-identical.
    /// </summary>
    public static RouteHandlerBuilder WithUKBatchAccess(this RouteHandlerBuilder builder, UKBatchAccessKind kind)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new UKBatchEndpointAccessMetadata(kind));
    }
}
