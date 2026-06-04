using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UKBatch.Worker.Tests.Common;
using Xunit;

namespace UKBatch.Worker.Tests;

/// <summary>
/// <c>UseWorkerMode</c> wiring: it sets <c>UKBatchOptions.ThisServiceName</c> to the
/// worker name, registers the heartbeat + guard hosted services, registers the heartbeat singleton, and
/// wires the named <c>HttpClient</c> with a slash-normalized base address. Asserted via the built
/// <see cref="ServiceProvider"/> / inspected <see cref="IServiceCollection"/> (no full host).
/// </summary>
public sealed class UseWorkerModeTests
{
    private static IServiceCollection BuildServices(Action<WorkerOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // UseWorkerMode calls BindConfiguration("UKBatch:Worker") → IConfiguration must be resolvable
        // when the options pipeline materializes (a real host always provides one).
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUKBatch(b => b.UseWorkerMode(configure));
        return services;
    }

    private static ServiceProvider BuildWorker(Action<WorkerOptions> configure)
        => BuildServices(configure).BuildServiceProvider();

    [Fact]
    public void UseWorkerMode_SetsThisServiceNameToWorkerName()
    {
        using var sp = BuildWorker(o =>
        {
            o.WorkerName = "invoicing";
            o.ServerUrl = "http://ukbatch-server:8080";
        });

        var options = sp.GetRequiredService<IOptions<UKBatchOptions>>().Value;
        options.ThisServiceName.Should().Be("invoicing",
            "the worker name becomes ThisServiceName so outbound JobMessage.SourceService is stamped");
    }

    [Fact]
    public void UseWorkerMode_RegistersHeartbeatAndGuardHostedServices()
    {
        // Inspect the IServiceCollection descriptors rather than resolving the IHostedService
        // enumerable — the latter would also materialize Core's UKBatchHost (needs an
        // IHostApplicationLifetime that a non-host test rig does not provide).
        var services = BuildServices(o =>
        {
            o.WorkerName = "invoicing";
            o.ServerUrl = "http://ukbatch-server:8080";
        });

        var hostedDescriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToArray();

        hostedDescriptors.Should().Contain(d => d.ImplementationType == typeof(WorkerTransportGuard),
            "the fail-fast transport guard is registered as a hosted service");

        // The heartbeat hosted descriptor is factory-based (it resolves the singleton), so assert the
        // factory descriptor exists alongside the WorkerHeartbeatService singleton registration.
        hostedDescriptors.Should().Contain(d => d.ImplementationFactory != null,
            "the heartbeat is registered as a hosted service via a singleton-resolving factory");
        services.Should().Contain(d => d.ServiceType == typeof(WorkerHeartbeatService),
            "the heartbeat background service is registered (as a singleton, shared with the hosted-service factory)");
    }

    [Fact]
    public void UseWorkerMode_RegistersHeartbeatSingleton()
    {
        using var sp = BuildWorker(o =>
        {
            o.WorkerName = "invoicing";
            o.ServerUrl = "http://ukbatch-server:8080";
        });

        var a = sp.GetService<WorkerHeartbeatService>();
        var b = sp.GetService<WorkerHeartbeatService>();
        a.Should().NotBeNull();
        a.Should().BeSameAs(b, "the heartbeat is a singleton (the hosted-service registration resolves the same instance)");
    }

    [Fact]
    public void UseWorkerMode_NamedHttpClient_HasSlashNormalizedBaseAddress()
    {
        using var sp = BuildWorker(o =>
        {
            o.WorkerName = "invoicing";
            o.ServerUrl = "http://ukbatch-server:8080"; // NO trailing slash
        });

        var factory = sp.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(WorkerHeartbeatService.HttpClientName);

        client.BaseAddress.Should().NotBeNull();
        client.BaseAddress!.ToString().Should().Be("http://ukbatch-server:8080/",
            "the named client base address is normalized to a trailing slash so 'api/workers/beat' resolves correctly");
    }

    [Fact]
    public void UseWorkerMode_RegistersOptionsValidator()
    {
        using var sp = BuildWorker(o =>
        {
            o.WorkerName = "invoicing";
            o.ServerUrl = "http://ukbatch-server:8080";
        });

        sp.GetServices<IValidateOptions<WorkerOptions>>()
            .OfType<WorkerOptionsValidator>()
            .Should().NotBeEmpty("the WorkerOptionsValidator is registered so misconfiguration fails fast");
    }

    [Fact]
    public void UseWorkerMode_BlankWorkerNameInCallback_ThrowsEagerly()
    {
        // The eager probe reads WorkerName synchronously to set ThisServiceName; a blank name is a
        // programmer error surfaced immediately (not deferred to options validation).
        var services = new ServiceCollection();
        services.AddLogging();

        Action act = () => services.AddUKBatch(b => b.UseWorkerMode(o => o.WorkerName = "   "));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WorkerName*");
    }

    [Fact]
    public void UseWorkerMode_NullConfigureCallback_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Action act = () => services.AddUKBatch(b => b.UseWorkerMode(null!));

        act.Should().Throw<ArgumentNullException>("the configure callback is guarded with ThrowIfNull");
    }
}
