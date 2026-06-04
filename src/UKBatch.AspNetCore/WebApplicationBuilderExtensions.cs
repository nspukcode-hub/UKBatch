using Microsoft.AspNetCore.Builder;
using UKBatch.Builders;

namespace UKBatch.AspNetCore;

/// <summary>Ergonomic wrapper that calls <see cref="ServiceCollectionExtensions.AddUKBatchAspNetCore"/> on
/// the builder's services and returns the builder for chaining.</summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>
    /// One-line registration for ASP.NET Core hosts. Equivalent to:
    /// <code>builder.Services.AddUKBatchAspNetCore(configure);</code>
    /// </summary>
    public static WebApplicationBuilder AddUKBatchAspNetCore(
        this WebApplicationBuilder builder,
        Action<UKBatchBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddUKBatchAspNetCore(configure);
        return builder;
    }
}
