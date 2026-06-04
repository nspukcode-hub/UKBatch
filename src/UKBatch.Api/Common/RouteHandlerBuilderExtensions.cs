using Microsoft.AspNetCore.Builder;

namespace UKBatch.Api.Common;

/// <summary>
/// Helper to apply an OpenAPI operation-id prefix uniformly across the
/// endpoint <c>Map</c> methods so the same surface can be mounted twice (e.g. <c>/api</c> +
/// <c>/api/secured</c>) without operation-id collisions.
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
}
