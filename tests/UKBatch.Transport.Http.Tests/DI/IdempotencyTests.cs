using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http;
using Xunit;

namespace UKBatch.Transport.Http.Tests.DI;

/// <summary>
/// <c>AddUKBatchHttpTransport</c> idempotency + orphan removal +
/// last-registered-wins ITransport replacement.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class IdempotencyTests
{
    private static IServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UKBatch:Transport:Http:SharedSecret"] = "TEST-SECRET-FOR-VALIDATION-FLOOR-32CH+",
                ["UKBatch:Transport:Http:DefaultRequestTimeout"] = "00:00:30",
                ["UKBatch:Transport:Http:LongPollMaxWait"] = "00:00:25",
            })
            .Build());
        return services;
    }

    [Fact]
    public void AddUKBatchHttpTransport_CalledTwice_RegistersOnce()
    {
        var services = BuildBaseServices();
        services.AddUKBatchHttpTransport();
        var beforeCount = services.Count(d => d.ServiceType == typeof(HttpTransport));
        services.AddUKBatchHttpTransport();
        var afterCount = services.Count(d => d.ServiceType == typeof(HttpTransport));
        beforeCount.Should().Be(1);
        afterCount.Should().Be(1, "idempotency guard prevents double-registration");
    }

    [Fact]
    public void AddUKBatchHttpTransport_RemovesOrphanInProcessTransportDescriptor()
    {
        // Simulate AddUKBatch having registered InProcessTransport as a concrete singleton.
        // After AddUKBatchHttpTransport, the descriptor MUST be removed (invariant).
        var services = BuildBaseServices();
        services.AddSingleton<InProcessTransport>();
        services.Any(d => d.ServiceType == typeof(InProcessTransport)).Should().BeTrue();

        services.AddUKBatchHttpTransport();
        services.Any(d => d.ServiceType == typeof(InProcessTransport)).Should().BeFalse(
 " orphan removal: InProcessTransport singleton descriptor is removed when HTTP transport supersedes it");
    }

    [Fact]
    public void AddUKBatchHttpTransport_AfterInProcessRegistration_ReplacesITransport()
    {
        // Replace path: an existing ITransport factory (InProcessTransport) is replaced by the HTTP
        // transport factory.
        var services = BuildBaseServices();
        services.AddSingleton<InProcessTransport>();
        services.AddSingleton<ITransport>(sp => sp.GetRequiredService<InProcessTransport>());

        services.AddUKBatchHttpTransport();

        using var sp = services.BuildServiceProvider();
        var transport = sp.GetRequiredService<ITransport>();
        transport.Should().BeOfType<HttpTransport>();
    }

    [Fact]
    public void AddUKBatchHttpTransport_BeforeAnyOtherTransport_RegistersHttpAsITransport()
    {
        var services = BuildBaseServices();
        // HTTP first — no orphan InProcess to remove.
        services.AddUKBatchHttpTransport();
        using var sp = services.BuildServiceProvider();
        var transport = sp.GetRequiredService<ITransport>();
        transport.Should().BeOfType<HttpTransport>();
    }

    [Fact]
    public void AddUKBatchHttpTransport_RegistersHmacFilter_AndDedupeCaches()
    {
        var services = BuildBaseServices();
        services.AddUKBatchHttpTransport();
        using var sp = services.BuildServiceProvider();
        // Internal types are visible via InternalsVisibleTo.
        sp.GetService(typeof(UKBatch.Transport.Http.Auth.HmacSignatureService)).Should().NotBeNull();
        sp.GetService(typeof(UKBatch.Transport.Http.Auth.NonceDedupeCache)).Should().NotBeNull();
        sp.GetService(typeof(UKBatch.Transport.Http.Endpoints.MessageIdDedupeCache)).Should().NotBeNull();
        sp.GetService(typeof(UKBatch.Transport.Http.Auth.HmacAuthorizationFilter)).Should().NotBeNull();
    }
}
