using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Builders;
using UKBatch.Runtime;

namespace UKBatch.AspNetCore.Tests.WebApplicationFactory;

/// <summary>
/// Minimal in-process host used by non-sample integration tests. Builds a fresh
/// <see cref="WebApplication"/> via <see cref="WebApplication.CreateBuilder()"/>, registers the
/// bridge package, and exposes the test server's <see cref="HttpClient"/>. Tests dispose the
/// returned host to tear down the runtime cleanly.
/// </summary>
public sealed class BasicWebHost : IAsyncDisposable
{
    /// <summary>The built <see cref="WebApplication"/>; null until <see cref="StartAsync"/> runs.</summary>
    public WebApplication? App { get; private set; }

    /// <summary>Test-server <see cref="HttpClient"/>; null until <see cref="StartAsync"/> runs.</summary>
    public HttpClient? Client { get; private set; }

    /// <summary>
    /// Builds + starts the host. <paramref name="configureUKBatch"/> registers the user's jobs and
    /// overrides options; <paramref name="configureServices"/> can register additional DI services
    /// (sinks, test-only types). <paramref name="configureEndpoints"/> maps minimal-API routes.
    /// </summary>
    public async Task StartAsync(
        Action<UKBatchBuilder>? configureUKBatch = null,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureEndpoints = null)
    {
        var builder = WebApplication.CreateBuilder();
        // Use the TestServer for in-memory HTTP plumbing.
        builder.WebHost.UseTestServer();

        builder.AddUKBatchAspNetCore(b =>
        {
            b.Configure(o =>
            {
                o.MaxDegreeOfParallelism = 2;
                o.DispatcherChannelCapacity = 64;
            });
            configureUKBatch?.Invoke(b);
        });

        configureServices?.Invoke(builder.Services);

        // Test-local DevAuth handler — the sample's handlers are internal; replicating ~20 LOC
        // is cleaner than InternalsVisibleTo across samples + tests.
        builder.Services
            .AddAuthentication(TestDevAuthSchemeOptions.SchemeName)
            .AddScheme<TestDevAuthSchemeOptions, TestDevAuthHandler>(TestDevAuthSchemeOptions.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        App = builder.Build();
        App.UseAuthentication();
        App.UseAuthorization();
        App.MapHealthChecks("/healthz");

        // Default minimal endpoints used by most tests; tests can map additional routes via the
        // configureEndpoints callback.
        App.MapGet("/trigger/{jobName}",
            async (IJobRunner runner, IJobTriggerContext idCtx, IJobTraceContext traceCtx, string jobName, CancellationToken ct) =>
            {
                var execution = await runner.TriggerWithRequestContextAsync(
                    idCtx, traceCtx, jobName, JobParameters.Empty, ct);
                return Results.Ok(new
                {
                    execution.ExecutionId,
                    execution.TriggeredBy,
                    Status = execution.Status.ToString(),
                });
            });

        configureEndpoints?.Invoke(App);

        await App.StartAsync().ConfigureAwait(false);
        Client = App.GetTestClient();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null)
        {
            await App.StopAsync().ConfigureAwait(false);
            await App.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Test-local DevAuth scheme options.</summary>
internal sealed class TestDevAuthSchemeOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "DevAuth";
}

/// <summary>Test-local DevAuth handler — reads X-Dev-User / X-Dev-Roles, sets identity.</summary>
internal sealed class TestDevAuthHandler : AuthenticationHandler<TestDevAuthSchemeOptions>
{
    public TestDevAuthHandler(
        IOptionsMonitor<TestDevAuthSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Dev-User", out var user) || string.IsNullOrEmpty(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var claims = new List<Claim> { new(ClaimTypes.Name, user!) };
        if (Request.Headers.TryGetValue("X-Dev-Roles", out var roles))
        {
            foreach (var r in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
