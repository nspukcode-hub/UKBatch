using Bunit;
using Bunit.TestDoubles;
using NSubstitute;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.State;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages.Common;

/// <summary>Shared bunit fixture helpers — mock IUKBatchClient + factory + registry + state.</summary>
internal static class PageTestHelpers
{
    /// <summary>
    /// Registers a signed-in principal that satisfies both the viewer and operator policies, mirroring
    /// the dashboard's open-default (auth-off) UI where every control is visible. Call it on any test
    /// that renders a component containing an <c>AuthorizeView</c> / <c>AuthorizeRouteView</c> so the
    /// cascading authentication state is present.
    /// </summary>
    public static void AddPermitAllAuth(this TestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("dashboard");
        auth.SetPolicies("UKBatch:Viewer", "UKBatch:Operator");
    }

    /// <summary>
    /// Registers a signed-in principal that satisfies the viewer policy but NOT the operator policy, so
    /// a test can assert that operator-gated write controls are hidden from a read-only user.
    /// </summary>
    public static void AddViewerOnlyAuth(this TestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("viewer");
        auth.SetPolicies("UKBatch:Viewer");
    }

    public static UKBatchServiceDescriptor Descriptor(string name) => new()
    {
        Name = name,
        BaseUrl = new Uri($"http://{name}.local:5000/api/"),
        DisplayName = name,
    };

    public static IUKBatchServiceRegistry RegistryWith(params UKBatchServiceDescriptor[] services)
    {
        var registry = Substitute.For<IUKBatchServiceRegistry>();
        registry.All().Returns(services.ToArray());
        foreach (var svc in services)
        {
            registry.TryGet(svc.Name).Returns(svc);
        }
        registry.TryGet(Arg.Is<string>(n => services.All(s => s.Name != n))).Returns((UKBatchServiceDescriptor?)null);
        return registry;
    }

    public static IUKBatchClientFactory FactoryFor(string name, IUKBatchClient client)
    {
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(name).Returns(client);
        return factory;
    }

    public static IUKBatchClient BuildClient(UKBatchClientState state = UKBatchClientState.Connected)
    {
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(state);
        return client;
    }

    public static IDashboardState NewState() => new ScopedDashboardStateForTests();

    public static Microsoft.Extensions.Options.IOptions<DashboardOptions> NewOptions(DashboardOptions? opts = null)
        => Microsoft.Extensions.Options.Options.Create(opts ?? new DashboardOptions());

    private sealed class ScopedDashboardStateForTests : IDashboardState
    {
        public UKBatchServiceDescriptor? CurrentService { get; set; }
        public DashboardTheme Theme { get; set; } = DashboardTheme.Dark;

#pragma warning disable CS0067 // event never raised — test stub
        public event Action<UKBatchServiceDescriptor?>? CurrentServiceChanged;
#pragma warning restore CS0067
    }

    public static INotificationService NewNotifications() => new TestNotifications();

    private sealed class TestNotifications : INotificationService
    {
#pragma warning disable CS0067 // event never used — test stub
        public event Func<Notification, Task>? OnNotification;
#pragma warning restore CS0067
        public Task NotifyAsync(Notification notification) => Task.CompletedTask;
        public Task SuccessAsync(string title, string? body = null) => Task.CompletedTask;
        public Task ErrorAsync(string title, string? body = null) => Task.CompletedTask;
        public Task WarningAsync(string title, string? body = null) => Task.CompletedTask;
        public Task InfoAsync(string title, string? body = null) => Task.CompletedTask;
    }
}
