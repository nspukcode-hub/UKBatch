using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Api.Hub;
using UKBatch.AspNetCore;
using Xunit;

namespace UKBatch.Api.Tests.Diagnostics;

/// <summary>
/// <c>AddUKBatchApi</c> must be idempotent.
/// Double-call MUST NOT register a second <see cref="JobStatusHubFanout"/> factory, otherwise
/// the host would invoke <c>StartAsync</c> twice on the same singleton — leaking three pump tasks.
/// </summary>
public sealed class ServiceCollectionIdempotencyTests
{
    private static IServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Wire up the minimum UKBatch.AspNetCore stack so AddUKBatchApi's fail-fast check passes.
        services.AddUKBatchAspNetCore(b => b.Configure(_ => { }));
        return services;
    }

    [Fact]
    public void AddUKBatchApi_CalledTwice_IsIdempotent()
    {
        var services = BuildBaseServices();

        services.AddUKBatchApi();
        services.AddUKBatchApi(); // second call must be a no-op.

        // Verify ONE JobStatusHubFanout singleton descriptor.
        var hubFanoutCount = services.Count(d => d.ServiceType == typeof(JobStatusHubFanout));
        hubFanoutCount.Should().Be(1,
 "AddUKBatchApi must register JobStatusHubFanout exactly once across multiple calls.");

        // Verify the IHostedService factory pointing at JobStatusHubFanout appears exactly once.
        var fanoutHostedServiceCount = services.Count(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationFactory is not null);
        fanoutHostedServiceCount.Should().Be(1,
 "the IHostedService factory for the fan-out pump must be registered once.");
    }

    [Fact]
    public async Task JobStatusHubFanout_StartAsyncCalledTwice_DoesNotLeakPumps()
    {
        // Defense-in-depth: if a misconfigured host invokes StartAsync twice on the same singleton
        // (e.g., a manually-registered IHostedService duplicate), the second call must be a no-op
        // and NOT spawn additional pump tasks. Pre-fix the second call would overwrite the task
        // references, leaking three pump Tasks (Watch, Approval, Progress) from the first call.

        var services = BuildBaseServices();
        services.AddUKBatchApi();

        // Add a SignalR registration manually — JobStatusHubFanout needs IHubContext<...> in DI.
        // ServiceCollection alone does not provision the hub context; we'll construct a minimal
        // host to ensure full DI graph resolves.
        var host = new HostBuilder()
            .ConfigureServices((_, s) =>
            {
                foreach (var d in services) s.Add(d);
            })
            .Build();

        var fanout = host.Services.GetRequiredService<JobStatusHubFanout>();
        using var cts = new CancellationTokenSource();

        // Start twice — second call must be a no-op (no leaked tasks, no overwritten CTS).
        await fanout.StartAsync(cts.Token);
        await fanout.StartAsync(cts.Token);

        // Inspect the private fields via reflection to assert no pump duplication.
        var watchField = typeof(JobStatusHubFanout).GetField("_watchPumpTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var approvalField = typeof(JobStatusHubFanout).GetField("_approvalPumpTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var progressField = typeof(JobStatusHubFanout).GetField("_progressPumpTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        // cleanup: lock the 4th pump task arm. Without this, a future
        // edit that bypasses the StartAsync early-return for BatchCompletionPumpAsync would leak
        // a 4th pump silently — the regression test from would still pass.
        var batchCompletionField = typeof(JobStatusHubFanout).GetField("_batchCompletionPumpTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var watchTask = (Task?)watchField!.GetValue(fanout);
        var approvalTask = (Task?)approvalField!.GetValue(fanout);
        var progressTask = (Task?)progressField!.GetValue(fanout);
        var batchCompletionTask = (Task?)batchCompletionField!.GetValue(fanout);

        watchTask.Should().NotBeNull("StartAsync arms the watch pump task on first call.");
        approvalTask.Should().NotBeNull("StartAsync arms the approval pump task on first call.");
        progressTask.Should().NotBeNull("StartAsync arms the progress pump task on first call.");
        batchCompletionTask.Should().NotBeNull("StartAsync arms the batch-completion pump task on first call.");

        await fanout.StopAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
        host.Dispose();
    }
}
