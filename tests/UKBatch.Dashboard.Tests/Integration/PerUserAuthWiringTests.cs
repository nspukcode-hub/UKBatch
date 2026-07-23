using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.Security;
using Xunit;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
/// Pins the DI shape of <c>AddUKBatchDashboard</c> in both modes. The per-user path must build under
/// scope validation without a captive-dependency error: the client factory becomes a per-circuit scoped
/// provider, and the singleton conductor (which would capture the scoped factory) is not registered.
/// </summary>
public sealed class PerUserAuthWiringTests
{
    private sealed class StubTokenAccessor : IUKBatchUserTokenAccessor
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            new((string?)null);
    }

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        return services;
    }

    private static void ConfigureOneService(DashboardOptions options) =>
        options.Services.Add(new UKBatchServiceDescriptor
        {
            Name = "svc",
            BaseUrl = new Uri("http://svc.local:5000/api/"),
        });

    [Fact]
    public async Task PerUserAuth_BuildsScopedClientProvider_NoConductor_NoCaptiveDependency()
    {
        var services = BaseServices();
        // Presence of the token accessor is the per-user signal.
        services.AddSingleton<IUKBatchUserTokenAccessor, StubTokenAccessor>();
        services.AddUKBatchDashboard(ConfigureOneService);

        // The conductor (a singleton) is not registered under per-user auth, so nothing captures the
        // scoped client factory — the trap ValidateScopes would flag.
        services.Any(d => d.ServiceType == typeof(UKBatchServiceConductor)).Should().BeFalse();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IUKBatchClientFactory>();
        factory.Should().BeOfType<PerCircuitUKBatchClientProvider>(
            "per-user auth binds a per-circuit client provider so each socket carries the user's identity");

        // With an authentication integration present, the permit-all fallback must be skipped: the real
        // Blazor authentication state provider (from AddInteractiveServerComponents) supplies the principal
        // that AuthorizeView / AuthorizeRouteView and the token accessor's circuit path read.
        var authStateProvider = scope.ServiceProvider.GetService<AuthenticationStateProvider>();
        authStateProvider.Should().NotBeNull("the interactive server components register a real provider");
        authStateProvider.Should().NotBeOfType<PermitAllAuthenticationStateProvider>(
            "an authentication integration replaces the auth-off permit-all provider");
    }

    [Fact]
    public async Task AuthOff_UsesSingletonFactoryAndConductor()
    {
        var services = BaseServices();
        services.AddUKBatchDashboard(ConfigureOneService);

        services.Any(d => d.ServiceType == typeof(UKBatchServiceConductor)).Should().BeTrue(
            "auth-off keeps the startup conductor that eagerly connects the shared-identity clients");

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        provider.GetRequiredService<IUKBatchClientFactory>().Should().BeOfType<UKBatchClientFactory>();

        // Auth-off registers the permit-all provider so the UI's authorization views render every control.
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetService<AuthenticationStateProvider>()
            .Should().BeOfType<PermitAllAuthenticationStateProvider>(
                "with no authentication integration the dashboard reports an all-roles principal");
    }
}
