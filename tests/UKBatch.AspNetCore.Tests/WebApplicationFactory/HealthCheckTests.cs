using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.Tests.Helpers;
using Xunit;

namespace UKBatch.AspNetCore.Tests.WebApplicationFactory;

/// <summary>
/// Acceptance tests for <c>UKBatchHealthCheck</c>. The check ships under the name <c>"ukbatch"</c>
/// with tags <c>["ukbatch", "ready"]</c> (S5 invariant — readiness, not liveness).
/// </summary>
public sealed class HealthCheckTests
{
    [Fact]
    public async Task Healthz_AfterStart_ReturnsHealthy()
    {
        await using var host = new BasicWebHost();
        await host.StartAsync(
            configureUKBatch: b => b.AddJob<TriggeredByCapturingJob>(),
            configureServices: s => s.AddSingleton<CapturedTriggeredBy>());

        var response = await host.Client!.GetAsync(new Uri("/healthz", UriKind.Relative));
        var body = await response.ShouldBeAsync(HttpStatusCode.OK);
        body.Should().Be("Healthy");
    }

    /// <summary>
    /// The single-signal check returns <see cref="HealthStatus.Unhealthy"/> before
    /// <c>ApplicationStarted</c> fires. We verify by calling the check directly with a fake
    /// lifetime that never signals "started".
    /// </summary>
    [Fact]
    public async Task Healthz_BeforeStart_ReturnsUnhealthy()
    {
        var lifetime = new NotStartedLifetime();
        var checkType = typeof(UKBatch.AspNetCore.ServiceCollectionExtensions).Assembly
            .GetType("UKBatch.AspNetCore.HealthChecks.UKBatchHealthCheck");
        checkType.Should().NotBeNull();
        var check = (IHealthCheck)Activator.CreateInstance(checkType!, lifetime)!;
        var ctx = new HealthCheckContext();

        var result = await check.CheckHealthAsync(ctx);
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().ContainKey("state").WhoseValue.Should().Be("starting");
    }

    /// <summary>S5 — the check is tagged <c>"ready"</c>, not <c>"live"</c>.</summary>
    [Fact]
    public void HealthCheck_TagIsReady_NotLive()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });
        builder.Services.AddSingleton<CapturedTriggeredBy>();
        using var app = builder.Build();

        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.FirstOrDefault(r => r.Name == "ukbatch");
        registration.Should().NotBeNull();
        registration!.Tags.Should().Contain("ready");
        registration.Tags.Should().NotContain("live");
        registration.Tags.Should().Contain("ukbatch");
    }
}

/// <summary>Test-only <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime"/> whose
/// <c>ApplicationStarted</c> token never signals — used to drive the Unhealthy branch.</summary>
internal sealed class NotStartedLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
