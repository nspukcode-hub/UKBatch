using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch;
using UKBatch.Builders;

namespace UKBatch.Core.Tests.Helpers;

/// <summary>
/// Helper for building an in-memory host wired with UKBatch and (optionally) test substitutes.
/// Tests should always create their own host so test isolation is preserved.
/// </summary>
internal static class TestHostBuilder
{
    public static IHostBuilder Create(Action<UKBatchBuilder>? configureUk = null, Action<IServiceCollection>? configureServices = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(lb => lb.ClearProviders().SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddUKBatch(b =>
                {
                    b.Configure(o =>
                    {
                        o.MaxDegreeOfParallelism = 2;
                        o.DispatcherChannelCapacity = 256;
                        o.ProgressFlushInterval = TimeSpan.FromMilliseconds(50);
                        o.ShutdownTimeout = TimeSpan.FromSeconds(5);
                    });
                    configureUk?.Invoke(b);
                });
                configureServices?.Invoke(services);
            });
    }

    public static async Task<IHost> StartAsync(Action<UKBatchBuilder>? configureUk = null, Action<IServiceCollection>? configureServices = null)
    {
        var host = Create(configureUk, configureServices).Build();
        await host.StartAsync().ConfigureAwait(false);
        return host;
    }

    /// <summary>
    /// Stops the host gracefully. Tolerates the shutdown-timeout OCE that surfaces when in-flight
    /// workers cannot drain within the grace period — acceptable in test teardown.
    /// </summary>
    public static async Task StopGracefullyAsync(IHost host, TimeSpan? timeout = null)
    {
        if (host is null) return;
        try
        {
            await host.StopAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timeout — acceptable in tests.
        }
    }
}
