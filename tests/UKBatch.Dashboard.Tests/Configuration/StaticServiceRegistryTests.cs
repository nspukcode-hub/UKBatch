using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using UKBatch.Dashboard.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Configuration;

// <summary> — StaticServiceRegistry snapshot + appsettings binding contract.</summary>
public sealed class StaticServiceRegistryTests
{
    private static IUKBatchServiceRegistry BuildRegistry(params UKBatchServiceDescriptor[] descriptors)
    {
        var opts = new DashboardOptions { Services = [.. descriptors] };
        return new StaticServiceRegistry(Options.Create(opts));
    }

    [Fact]
    public void All_ReturnsServicesInRegistrationOrder()
    {
        var registry = BuildRegistry(
            new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") },
            new UKBatchServiceDescriptor { Name = "beta", BaseUrl = new Uri("http://b/api") },
            new UKBatchServiceDescriptor { Name = "gamma", BaseUrl = new Uri("http://c/api") });
        var all = registry.All();
        all.Select(d => d.Name).Should().Equal("alpha", "beta", "gamma");
    }

    [Fact]
    public void TryGet_KnownName_ReturnsDescriptor()
    {
        var registry = BuildRegistry(
            new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") });
        registry.TryGet("alpha").Should().NotBeNull();
        registry.TryGet("alpha")!.Name.Should().Be("alpha");
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsNull()
    {
        var registry = BuildRegistry(
            new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") });
        registry.TryGet("gamma").Should().BeNull();
    }

    [Fact]
    public void TryGet_EmptyName_Throws()
    {
        var registry = BuildRegistry(
            new UKBatchServiceDescriptor { Name = "alpha", BaseUrl = new Uri("http://a/api") });
        Action act = () => registry.TryGet(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ConfigBinding_FromAppsettings_PopulatesServices()
    {
        // echo: Services must be List<T> (not IReadOnlyList<T>) for the
        // ConfigurationBinder to populate it. Lock test.
        var configJson = """
        {
          "UKBatch": {
            "Dashboard": {
              "Services": [
                { "Name": "alpha", "BaseUrl": "http://localhost:5000/api" },
                { "Name": "beta",  "BaseUrl": "http://localhost:5001/api" }
              ]
            }
          }
        }
        """;
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(configJson)))
            .Build();
        var opts = new DashboardOptions();
        config.GetSection("UKBatch:Dashboard").Bind(opts);

        opts.Services.Should().HaveCount(2);
        opts.Services[0].Name.Should().Be("alpha");
        opts.Services[1].BaseUrl.Should().Be(new Uri("http://localhost:5001/api"));
    }
}
