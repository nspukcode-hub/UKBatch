using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.AspNetCore.DevAuth;
using UKBatch.AspNetCore.Tests.Helpers;
using Xunit;

namespace UKBatch.AspNetCore.Tests;

/// <summary>
/// Acceptance tests for the opt-in development-only auth helper <c>AddUKBatchDevAuth</c>: it registers
/// the header-trusting "DevAuth" scheme + authorization, is idempotent, fails closed in the Production
/// environment (unless overridden), and logs a loud warning whenever it is active.
/// </summary>
public sealed class DevAuthExtensionTests
{
    [Fact]
    public void AddUKBatchDevAuth_RegistersSchemeAndAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddUKBatchDevAuth();

        using var app = builder.Build();

        // The "DevAuth" scheme is registered.
        var schemes = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value.Schemes;
        schemes.Should().Contain(s => s.Name == DevAuthSchemeOptions.SchemeName);

        // Authorization is wired (the policy provider resolves).
        app.Services.GetService<IAuthorizationPolicyProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddUKBatchDevAuth_CalledTwice_DoesNotThrow()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Calling the helper more than once must be a no-op for the second call (AddScheme would
        // otherwise reject the duplicate scheme name).
        builder.Services.AddUKBatchDevAuth();
        builder.Services.AddUKBatchDevAuth();

        var act = () => builder.Build();

        act.Should().NotThrow("AddUKBatchDevAuth must be idempotent");
    }

    [Fact]
    public async Task SecuredEndpoint_WithOpsRoleHeader_Succeeds()
    {
        await using var host = await StartDevAuthHostAsync();
        using var client = host.Client.WithDevAuth("alice", "ops");

        var resp = await client.GetAsync(new Uri("/secured", UriKind.Relative));

        var body = await resp.ShouldBeAsync(HttpStatusCode.OK);
        body.Should().Be("alice");
    }

    [Fact]
    public async Task SecuredEndpoint_WithWrongRole_Returns403()
    {
        await using var host = await StartDevAuthHostAsync();
        using var client = host.Client.WithDevAuth("bob", "viewer");

        var resp = await client.GetAsync(new Uri("/secured", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an authenticated caller lacking the 'ops' role is forbidden");
    }

    [Fact]
    public async Task SecuredEndpoint_NoHeader_IsUnauthenticated()
    {
        await using var host = await StartDevAuthHostAsync();

        // No X-Dev-User header → the handler returns NoResult → the caller is unauthenticated.
        var resp = await host.Client.GetAsync(new Uri("/secured", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "without the dev-auth header the caller has no identity");
    }

    [Fact]
    public async Task Production_NoOverride_StartupThrows()
    {
        // The startup guard fails closed in Production: the header-trusting scheme must never run in
        // production without an explicit override, so the host fails to start.
        var (app, _) = BuildHost(environment: "Production", configureDevAuth: null);
        await using var _app = app;

        var act = async () => await app.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>(
            "dev auth must be refused in Production unless explicitly overridden");
    }

    [Fact]
    public async Task Production_WithAllowInProduction_StartsAndLogsWarning()
    {
        var (app, logs) = BuildHost(
            environment: "Production",
            configureDevAuth: o => o.AllowInProduction = true);
        await using var _app = app;

        var act = async () => await app.StartAsync();
        await act.Should().NotThrowAsync(
            "AllowInProduction = true overrides the fail-closed guard");

        logs.HasHeaderTrustingWarning().Should().BeTrue(
            "the helper must loudly warn that the header-trusting scheme is active");
    }

    [Fact]
    public async Task NonProduction_StartsAndLogsWarning()
    {
        var (app, logs) = BuildHost(environment: "Development", configureDevAuth: null);
        await using var _app = app;

        var act = async () => await app.StartAsync();
        await act.Should().NotThrowAsync("dev auth is allowed outside Production");

        logs.HasHeaderTrustingWarning().Should().BeTrue(
            "the helper must loudly warn that the header-trusting scheme is active");
    }

    /// <summary>
    /// Builds a TestServer host with the dev-auth helper registered and a single role-gated endpoint,
    /// starts it, and returns a started host whose <see cref="DevAuthHost.Client"/> is ready to use.
    /// </summary>
    private static async Task<DevAuthHost> StartDevAuthHostAsync()
    {
        // Development environment: the dev-auth guard fails closed in Production, and a host built under
        // the test runner would otherwise default to Production. A demo runs in Development.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddUKBatchDevAuth();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // Role-gated endpoint: requires the "ops" role (granted via X-Dev-Roles: ops). Returns the
        // resolved identity name so the success path can assert the principal was built correctly.
        app.MapGet("/secured", (HttpContext ctx) => Results.Text(ctx.User.Identity?.Name ?? "(none)"))
            .RequireAuthorization(policy => policy.RequireRole("ops"));

        await app.StartAsync();
        return new DevAuthHost(app);
    }

    /// <summary>
    /// Builds (but does NOT start) a TestServer host in the given environment with the dev-auth helper
    /// registered and a capturing logger attached. The caller starts it to exercise the startup guard.
    /// </summary>
    private static (WebApplication App, CapturingLoggerProvider Logs) BuildHost(
        string environment,
        Action<UKBatchDevAuthOptions>? configureDevAuth)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });
        builder.WebHost.UseTestServer();

        var logs = new CapturingLoggerProvider();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        builder.Services.AddUKBatchDevAuth(configureDevAuth ?? (_ => { }));

        return (builder.Build(), logs);
    }

    /// <summary>Started TestServer host wrapper that disposes the app and exposes its client.</summary>
    private sealed class DevAuthHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public DevAuthHost(WebApplication app)
        {
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ILoggerProvider"/> that records emitted log entries so tests can
    /// assert on the loud dev-auth warning without depending on a separate testing package.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public bool HasHeaderTrustingWarning()
        {
            lock (_entries)
            {
                return _entries.Any(e =>
                    e.Level == LogLevel.Warning
                    && e.Message.Contains("header-trusting", StringComparison.OrdinalIgnoreCase));
            }
        }

        private void Add(LogLevel level, string message)
        {
            lock (_entries)
            {
                _entries.Add((level, message));
            }
        }

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;

            public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _owner.Add(logLevel, formatter(state, exception));
            }
        }
    }
}
