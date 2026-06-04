using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Diagnostics;

// <summary> Idempotency invariants — AddUKBatchDashboard mirrors.</summary>
public sealed class ServiceCollectionIdempotencyTests
{
    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        return services;
    }

    [Fact]
    public void AddUKBatchDashboard_CalledTwice_IsIdempotent()
    {
        var services = BuildServices();
        services.AddUKBatchDashboard(opts =>
        {
            opts.Services.Add(new UKBatchServiceDescriptor
            {
                Name = "self",
                BaseUrl = new Uri("http://localhost:5000/api"),
            });
        });
        var conductorCountAfterFirst = services.Count(d => d.ServiceType == typeof(UKBatchServiceConductor));

        services.AddUKBatchDashboard(opts =>
        {
            opts.Services.Add(new UKBatchServiceDescriptor
            {
                Name = "other",
                BaseUrl = new Uri("http://localhost:5001/api"),
            });
        });
        var conductorCountAfterSecond = services.Count(d => d.ServiceType == typeof(UKBatchServiceConductor));
        var hostedServiceCountAfterSecond = services.Count(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationFactory is not null);

        conductorCountAfterFirst.Should().Be(1);
        conductorCountAfterSecond.Should().Be(1);
        hostedServiceCountAfterSecond.Should().Be(1, "double-registration would double-StartAsync the conductor");
    }

    [Fact]
    public void AddUKBatchDashboard_RegistersExpectedSingletons()
    {
        var services = BuildServices();
        services.AddUKBatchDashboard(opts =>
        {
            opts.Services.Add(new UKBatchServiceDescriptor
            {
                Name = "self",
                BaseUrl = new Uri("http://localhost:5000/api"),
            });
        });

        services.Should().Contain(d => d.ServiceType == typeof(IUKBatchServiceRegistry));
        services.Should().Contain(d => d.ServiceType == typeof(IUKBatchClientFactory));
        services.Should().Contain(d => d.ServiceType == typeof(UKBatchServiceConductor));
    }
}
