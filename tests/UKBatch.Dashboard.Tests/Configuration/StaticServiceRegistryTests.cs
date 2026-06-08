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
    public void BaseUrl_WithoutTrailingSlash_NormalizedToHaveSlash()
    {
        var descriptor = new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = new Uri("http://x:5000/api"),
        };
        descriptor.BaseUrl.AbsoluteUri.Should().EndWith("/");
        descriptor.BaseUrl.AbsoluteUri.Should().Be("http://x:5000/api/");
    }

    [Fact]
    public void BaseUrl_AlreadySlashed_UnchangedNoDoubleSlash()
    {
        var descriptor = new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = new Uri("http://x:5000/api/"),
        };
        descriptor.BaseUrl.AbsoluteUri.Should().Be("http://x:5000/api/");
        descriptor.BaseUrl.AbsoluteUri.Should().NotContain("api//");
    }

    [Fact]
    public void NormalizedBaseUrl_ResolvesRelativePath_PreservingApiSegment()
    {
        // The REST client uses bare relative paths (e.g. "jobs") against HttpClient.BaseAddress.
        // With the trailing slash normalized in, RFC 3986 resolution keeps the /api segment.
        var descriptor = new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = new Uri("http://x:5000/api"),
        };
        using var http = new HttpClient { BaseAddress = descriptor.BaseUrl };
        var resolved = new Uri(http.BaseAddress!, "jobs");
        resolved.AbsoluteUri.Should().Be("http://x:5000/api/jobs");
    }

    [Fact]
    public void NormalizedBaseUrl_DerivesHubUrl_PreservingApiSegment()
    {
        // Mirrors RestUKBatchClient hub-URL derivation: new Uri(BaseUrl, HubPath.TrimStart('/')).
        // Normalization makes the /api segment survive so the hub lands at /api/hubs/jobs.
        var descriptor = new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = new Uri("http://x:5000/api"),
            HubPath = "/hubs/jobs",
        };
        var hubUrl = new Uri(descriptor.BaseUrl, descriptor.HubPath.TrimStart('/'));
        hubUrl.AbsoluteUri.Should().Be("http://x:5000/api/hubs/jobs");
    }

    [Fact]
    public void BaseUrl_DiffersOnlyByTrailingSlash_DescriptorsCompareEqual()
    {
        // Record value-equality: a slashed and slash-less BaseUrl normalize to the same value.
        var a = new UKBatchServiceDescriptor { Name = "self", BaseUrl = new Uri("http://x:5000/api") };
        var b = new UKBatchServiceDescriptor { Name = "self", BaseUrl = new Uri("http://x:5000/api/") };
        a.Should().Be(b);
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
        // BaseUrl auto-normalizes a missing trailing slash, so the bound value gains the slash.
        opts.Services[1].BaseUrl.Should().Be(new Uri("http://localhost:5001/api/"));
    }
}
