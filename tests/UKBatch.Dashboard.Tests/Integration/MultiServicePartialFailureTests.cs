using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages;
using UKBatch.Dashboard.Components.Shared;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.State;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
///  (multi-service partial failure) integration coverage. Guards the
/// "one service down at boot does NOT stop other services or blank the Landing page" contract.
/// </summary>
public sealed class MultiServicePartialFailureTests
{
    [Fact]
    public async Task Conductor_OneServiceDownAtBoot_OthersContinueAndHostBoots()
    {
        // invariant: one service URL is intentionally unreachable. The host MUST still come up
        // (conductor swallows initial-connect failures + relies on the periodic retry loop).
        using var factory = new MultiServiceFactory(new[]
        {
            // Healthy descriptor — loopback to TestServer is overridden via ConfigureWebHost.
            ("self", "http://localhost/api/"),
            // Intentionally unreachable. localhost:65535 is reserved; connection refused fast.
            ("dead", "http://127.0.0.1:65535/api/"),
        });
        using var client = factory.CreateClient();

        // If the conductor propagated failures, BootClient creation would throw. The fact that
        // CreateClient returns a usable HttpClient + GET /dashboard returns 200 proves the host
        // booted with one failed service descriptor.
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        // Both service entries render in the sidebar (one degraded card per failed service).
        html.Should().Contain("self");
        html.Should().Contain("dead");
    }

    [Fact]
    public void Landing_OneServiceDown_StillRendersOthers()
    {
        // bunit version of the contract — exercises the sequential fan-in loop in
        // Landing.razor. The sequential pattern means one throwing service does NOT
        // short-circuit the iteration.
        using var ctx = new TestContext();

        var ok = PageTestHelpers.Descriptor("billing");
        var down = PageTestHelpers.Descriptor("inventory");
        var registry = PageTestHelpers.RegistryWith(ok, down);

        var okClient = PageTestHelpers.BuildClient(UKBatchClientState.Connected);
        okClient.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobDefinitionDto>
            {
                Items = Array.Empty<JobDefinitionDto>(),
                TotalCount = 11,
                Offset = 0,
                Limit = 0,
            });
        okClient.ListBatchesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<UKBatch.Abstractions.Batches.BatchSource?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<BatchDefinitionDto>
            {
                Items = Array.Empty<BatchDefinitionDto>(),
                TotalCount = 4,
                Offset = 0,
                Limit = 0,
            });
        okClient.ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingApprovalDto>());

        var downClient = PageTestHelpers.BuildClient(UKBatchClientState.Disconnected);
        downClient.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns<Task<PageEnvelope<JobDefinitionDto>>>(_ =>
                throw new UKBatchClientException("connection refused", HttpStatusCode.ServiceUnavailable));

        var clientFactory = Substitute.For<IUKBatchClientFactory>();
        clientFactory.GetClient("billing").Returns(okClient);
        clientFactory.GetClient("inventory").Returns(downClient);

        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton(clientFactory);
        ctx.Services.AddSingleton(PageTestHelpers.NewState());

        var cut = ctx.RenderComponent<Landing>();

        cut.WaitForAssertion(() =>
        {
            // Healthy service exposes its counts; degraded service surfaces an ErrorBanner.
            cut.Markup.Should().Contain("billing");
            cut.Markup.Should().Contain("inventory");
            cut.Markup.Should().Contain("11");  // billing job count
            cut.Markup.Should().Contain("connection refused");  // ErrorBanner message
        });
    }

    [Fact]
    public void ServiceHealthDot_DisconnectedService_RendersDisconnectedClass()
    {
        // lock the CSS class contract — Disconnected ⇒ "service-health-dot--disconnected".
        // This is the marker the Landing degraded card relies on (and the Sidebar status indicator).
        using var ctx = new TestContext();
        var cut = ctx.RenderComponent<ServiceHealthDot>(parameters => parameters
            .Add(p => p.State, UKBatchClientState.Disconnected));
        cut.Markup.Should().Contain("service-health-dot--disconnected");
        cut.Markup.Should().Contain("Service disconnected");
    }

    /// <summary>
    /// Dedicated <see cref="WebApplicationFactory{T}"/> wrapper that configures multiple services
    /// with explicit BaseUrls — used by <see cref="Conductor_OneServiceDownAtBoot_OthersContinueAndHostBoots"/>.
    /// </summary>
    private sealed class MultiServiceFactory : WebApplicationFactory<Sample.Dashboard.Program>
    {
        private readonly IReadOnlyList<(string Name, string BaseUrl)> _services;

        public MultiServiceFactory(IReadOnlyList<(string Name, string BaseUrl)> services)
        {
            _services = services;
            Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var entries = new Dictionary<string, string?>();
                for (var i = 0; i < _services.Count; i++)
                {
                    entries[$"UKBatch:Dashboard:Services:{i}:Name"] = _services[i].Name;
                    entries[$"UKBatch:Dashboard:Services:{i}:BaseUrl"] = _services[i].BaseUrl;
                    entries[$"UKBatch:Dashboard:Services:{i}:DisplayName"] = _services[i].Name;
                }
                config.AddInMemoryCollection(entries);
            });
        }
    }
}
