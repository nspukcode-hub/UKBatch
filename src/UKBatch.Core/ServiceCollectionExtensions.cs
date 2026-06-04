using Microsoft.Extensions.DependencyInjection;
using UKBatch.Builders;

namespace UKBatch;

/// <summary>Entry point for UKBatch DI registration.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the UKBatch runtime with default in-memory storage and in-process transport.
    /// Configure jobs, batches, storage, and transport via the <paramref name="configure"/> callback.
    /// </summary>
    /// <remarks>
    /// Equivalent to calling <see cref="UKBatchBuilder.UseInMemoryStorage"/> and
    /// <see cref="UKBatchBuilder.UseInProcessTransport"/> by default; adapter packages override these.
    /// Also registers <c>IValidateOptions&lt;UKBatchOptions&gt;</c> to enforce sane defaults at host start.
    /// </remarks>
    public static IServiceCollection AddUKBatch(
        this IServiceCollection services,
        Action<UKBatchBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new UKBatchBuilder(services);
        // Default storage + transport BEFORE user configure so the user may override.
        builder.UseInMemoryStorage();
        builder.UseInProcessTransport();
        configure(builder);
        builder.Complete();
        return services;
    }
}
