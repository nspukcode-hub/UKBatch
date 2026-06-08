using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Layout;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.State;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Locks the shell layout's reaction to <see cref="IDashboardState.CurrentServiceChanged"/>. The
/// header breadcrumb (and the sidebar/banner it owns) read <see cref="IDashboardState.CurrentService"/>
/// from this layout, so the layout MUST subscribe and repaint when a page swaps the current service or
/// clears it to null — otherwise the breadcrumb/nav lag one navigation behind. The SUT here is the REAL
/// <see cref="DashboardState"/> (it owns the event); the registry/client-factory the child components
/// need are substituted.
/// </summary>
public sealed class MainLayoutTests : TestContext
{
    private const string Svc = "billing";

    public MainLayoutTests()
    {
        // The toast container + sidebar are purely presentational here; loose JS keeps any incidental
        // interop a no-op rather than a STRICT-mode failure.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private DashboardState WireDeps()
    {
        // REAL DashboardState — it is the SUT: setting CurrentService must raise CurrentServiceChanged,
        // and the layout's subscription must drive the re-render. A stubbed state with a never-raised
        // event would make this test pass vacuously.
        var state = new DashboardState();

        var descriptor = new UKBatchServiceDescriptor
        {
            Name = Svc,
            BaseUrl = new Uri($"http://{Svc}.local:5000/api/"),
            DisplayName = "Billing service",
        };

        var registry = Substitute.For<IUKBatchServiceRegistry>();
        registry.All().Returns(new[] { descriptor });
        registry.TryGet(Svc).Returns(descriptor);

        // ServiceSidebar + ConnectionBanner resolve one client per service from the factory; a connected
        // substitute renders the health dot / no banner without any real connection.
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(UKBatchClientState.Connected);
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);

        Services.AddSingleton<IDashboardState>(state);
        Services.AddSingleton(registry);
        Services.AddSingleton(factory);
        Services.AddSingleton(PageTestHelpers.NewNotifications());   // ToastContainer dependency
        return state;
    }

    private static UKBatchServiceDescriptor Descriptor(string name, string? display) => new()
    {
        Name = name,
        BaseUrl = new Uri($"http://{name}.local:5000/api/"),
        DisplayName = display,
    };

    [Fact]
    public void SettingCurrentService_RepaintsBreadcrumb_WithoutManualNudge()
    {
        var state = WireDeps();
        var cut = RenderComponent<MainLayout>();

        // No service selected → breadcrumb shows the "UKBatch" fallback.
        cut.Find(".dashboard-header__breadcrumb-service").TextContent.Should().Be("UKBatch");

        // Set the current service. The ONLY thing that can repaint the layout here is its new
        // subscription to CurrentServiceChanged — bunit does not re-render on its own.
        cut.InvokeAsync(() => state.CurrentService = Descriptor(Svc, "Billing service"));

        cut.Find(".dashboard-header__breadcrumb-service").TextContent.Should().Be(
            "Billing service",
            "the layout subscribes to CurrentServiceChanged and repaints — no page nudge required");
    }

    [Fact]
    public void ClearingCurrentServiceToNull_FallsBackToUKBatch_Immediately()
    {
        // Reproduces the Settings page clearing the current service: the layout must hear it and revert
        // the breadcrumb to the fallback on the same frame, not one navigation later.
        var state = WireDeps();
        state.CurrentService = Descriptor(Svc, "Billing service");
        var cut = RenderComponent<MainLayout>();

        cut.Find(".dashboard-header__breadcrumb-service").TextContent.Should().Be("Billing service");

        cut.InvokeAsync(() => state.CurrentService = null);

        cut.Find(".dashboard-header__breadcrumb-service").TextContent.Should().Be(
            "UKBatch",
            "clearing the service repaints the breadcrumb to the fallback immediately");
    }

    [Fact]
    public void DisplayNameNull_BreadcrumbFallsBackToName()
    {
        // The breadcrumb shows DisplayName ?? Name; a descriptor without a display name still repaints.
        var state = WireDeps();
        var cut = RenderComponent<MainLayout>();

        cut.InvokeAsync(() => state.CurrentService = Descriptor("orders", display: null));

        cut.Find(".dashboard-header__breadcrumb-service").TextContent.Should().Be("orders");
    }

    [Fact]
    public void Dispose_Unsubscribes_NoLateCallback_NoThrow()
    {
        // Subscription symmetry: after the layout is disposed, a state change must NOT reach the handler
        // (which would call StateHasChanged on a torn-down component). Proven two ways: the change does
        // not throw on the caller, and it does not trigger a further render of the disposed component.
        var state = WireDeps();
        var cut = RenderComponent<MainLayout>();
        var rendersBeforeDispose = cut.RenderCount;

        cut.Instance.Dispose();

        var act = () => state.CurrentService = Descriptor(Svc, "Billing service");
        act.Should().NotThrow("Dispose unsubscribes, so a post-dispose state change is a no-op for the layout");
        cut.RenderCount.Should().Be(
            rendersBeforeDispose,
            "the disposed layout no longer reacts to CurrentServiceChanged (handler was removed)");
    }

    [Fact]
    public void LiveLayout_BeforeDispose_DoesReRenderOnChange()
    {
        // Counterpart to the dispose test: while alive, a state change DOES bump the render count. This
        // pins that the dispose-test's "no extra render" result is caused by unsubscribe, not by the
        // event simply never re-rendering the layout.
        var state = WireDeps();
        var cut = RenderComponent<MainLayout>();
        var rendersBefore = cut.RenderCount;

        cut.InvokeAsync(() => state.CurrentService = Descriptor(Svc, "Billing service"));

        cut.RenderCount.Should().BeGreaterThan(
            rendersBefore,
            "a live layout re-renders when the current service changes");
    }
}
