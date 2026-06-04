using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.Triggering;
using UKBatch.AspNetCore.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.AspNetCore.Tests.WebApplicationFactory;

/// <summary>
/// Composition tests for <see cref="ServiceCollectionExtensions.AddUKBatchAspNetCore"/>.
/// Covers (a) implicit core registration via the configure callback, (b) the no-args bridge
/// + explicit <c>AddUKBatch</c> pattern, and (c) the S3 double-registration guard.
/// </summary>
public sealed class AddUKBatchAspNetCoreCompositionTests
{
    [Fact]
    public void WithConfigureCallback_RegistersCoreAndBridge()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });
        builder.Services.AddSingleton<CapturedTriggeredBy>();
        using var app = builder.Build();

        app.Services.GetService<IJobRunner>().Should().NotBeNull();
        app.Services.GetService<IJobTriggerContext>().Should().NotBeNull();
        app.Services.GetService<IJobTraceContext>().Should().NotBeNull();
    }

    [Fact]
    public void WithoutCallback_RequiresPriorAddUKBatch()
    {
        // No-args bridge on top of explicit AddUKBatch — the supported "two-step" pattern.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddUKBatch(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });
        builder.Services.AddSingleton<CapturedTriggeredBy>();
        // Bridge call with no args — does NOT trip the double-registration guard.
        builder.Services.AddUKBatchAspNetCore();
        using var app = builder.Build();

        app.Services.GetService<IJobRunner>().Should().NotBeNull();
        app.Services.GetService<IJobTriggerContext>().Should().NotBeNull();
    }

    [Fact]
    public void DoubleRegistration_ThrowsInvalidOperationException()
    {
        // S3 — calling AddUKBatchAspNetCore(configure) after AddUKBatch already ran must throw.
        var services = new ServiceCollection();
        services.AddUKBatch(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });

        var act = () => services.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<TriggeredByCapturingJob>();
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddUKBatchAspNetCore was called with a configure callback*");
    }
}
