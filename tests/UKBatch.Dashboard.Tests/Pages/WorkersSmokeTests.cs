using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Workers;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

/// <summary>
/// SMOKE assertions for the Workers panel — author self-verification only. The
/// comprehensive panel coverage (online/offline badge variants, polling, parameter binding,
/// HttpClient asset regression) is owned by the regression tests. These two facts guard the happy-path render +
/// empty-state so the page is not shipped broken.
/// </summary>
public sealed class WorkersSmokeTests : TestContext
{
    private void Register(string serviceName, IUKBatchClient client)
    {
        var svc = PageTestHelpers.Descriptor(serviceName);
        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(serviceName, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(TimeProvider.System);
    }

    [Fact]
    public void Render_OnlineWorker_ShowsNameAndOnlineBadge()
    {
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new()
            {
                Name = "invoicing",
                Jobs = ["GenerateInvoice"],
                Tags = ["billing"],
                Status = WorkerStatus.Online,
                LastSeenUtc = DateTimeOffset.UtcNow,
                Online = true,
            },
        });
        Register("billing", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "billing"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("invoicing"));
        cut.Markup.Should().Contain("worker-badge--online");
        cut.Markup.Should().Contain("GenerateInvoice"); // job chip
        cut.Markup.Should().Contain("billing");          // tag chip
    }

    [Fact]
    public void Render_NoWorkers_ShowsEmptyState()
    {
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkerInfo>());
        Register("orders", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "orders"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No workers reporting"));
        cut.Markup.Should().NotContain("worker-badge--online");
    }
}
