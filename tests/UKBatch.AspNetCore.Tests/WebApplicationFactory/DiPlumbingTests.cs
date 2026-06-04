using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.Triggering;
using UKBatch.AspNetCore.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.AspNetCore.Tests.WebApplicationFactory;

/// <summary>
/// DI plumbing assertions for <c>AddUKBatchAspNetCore</c>. Verifies that
/// <see cref="IJobRunner"/>, <see cref="IJobTriggerContext"/>, and <see cref="IJobTraceContext"/>
/// resolve cleanly and share a singleton instance.
/// </summary>
public sealed class DiPlumbingTests
{
    [Fact]
    public void Resolves_IJobRunner_FromRootProvider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });
        builder.Services.AddSingleton<CapturedTriggeredBy>();
        using var app = builder.Build();

        var runner = app.Services.GetRequiredService<IJobRunner>();
        runner.Should().NotBeNull();
    }

    [Fact]
    public void Resolves_IJobTriggerContext_AndIJobTraceContext_SameSingleton()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });
        builder.Services.AddSingleton<CapturedTriggeredBy>();
        using var app = builder.Build();

        var triggerCtx = app.Services.GetRequiredService<IJobTriggerContext>();
        var traceCtx = app.Services.GetRequiredService<IJobTraceContext>();
        // S1 invariant: same concrete singleton instance.
        ReferenceEquals(triggerCtx, traceCtx).Should().BeTrue(
            "IJobTriggerContext and IJobTraceContext must resolve to the same singleton.");
    }

    /// <summary>N3 — verify the container builds even with <c>ValidateScopes=true</c>.</summary>
    [Fact]
    public void BuildServiceProvider_WithValidateScopes_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The host normally registers IHostApplicationLifetime; supply a stub so the validate
        // path doesn't fail when constructing UKBatchHost / UKBatchHealthCheck.
        services.AddSingleton<IHostApplicationLifetime, StubHostLifetime>();
        services.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });
        services.AddSingleton<CapturedTriggeredBy>();

        var act = () =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            // Touch the IJobRunner so any lazy validation runs.
            _ = provider.GetRequiredService<IJobRunner>();
        };
        act.Should().NotThrow();
    }
}

/// <summary>Stub <see cref="IHostApplicationLifetime"/> used by <c>DiPlumbingTests</c>.</summary>
internal sealed class StubHostLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
