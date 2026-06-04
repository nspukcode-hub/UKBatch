using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sample.SimpleJob.DevAuth;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.Triggering;
using UKBatch.AspNetCore.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.AspNetCore.Tests.WebApplicationFactory;

/// <summary>
/// Integration tests for the HttpContext-aware TriggeredBy enrichment. The DevAuth handler from
/// <c>Sample.SimpleJob</c> is reused so the <c>X-Dev-User</c> header drives
/// <see cref="HttpContext.User.Identity.Name"/>.
/// </summary>
public sealed class HttpContextEnricherTests
{
    private const string JobName = nameof(TriggeredByCapturingJob);

    private static async Task<BasicWebHost> StartHostAsync()
    {
        var host = new BasicWebHost();
        await host.StartAsync(
            configureUKBatch: b => b.AddJob<TriggeredByCapturingJob>().Named(JobName),
            configureServices: s => s.AddSingleton<CapturedTriggeredBy>());
        return host;
    }

    [Fact]
    public async Task XDevUser_alice_PopulatesTriggeredBy()
    {
        await using var host = await StartHostAsync();
        host.Client!.WithDevAuth("alice");

        var response = await host.Client!.GetAsync(new Uri($"/trigger/{JobName}", UriKind.Relative));
        var body = await response.ShouldBeAsync(HttpStatusCode.OK);
        body.Should().Contain("\"triggeredBy\":\"alice\"");

        var capturedSink = host.App!.Services.GetRequiredService<CapturedTriggeredBy>();
        var captured = await capturedSink.WaitAsync(TimeSpan.FromSeconds(60));
        captured.Should().Be("alice");
    }

    [Fact]
    public async Task NoHeader_ProducesNullTriggeredBy()
    {
        await using var host = await StartHostAsync();
        // No DevAuth headers => HttpContext.User has no identity name => null TriggeredBy.

        var response = await host.Client!.GetAsync(new Uri($"/trigger/{JobName}", UriKind.Relative));
        var body = await response.ShouldBeAsync(HttpStatusCode.OK);
        // null is serialized as `null` in System.Text.Json.
        body.Should().Contain("\"triggeredBy\":null");

        var capturedSink = host.App!.Services.GetRequiredService<CapturedTriggeredBy>();
        var captured = await capturedSink.WaitAsync(TimeSpan.FromSeconds(60));
        captured.Should().BeNull();
    }

    [Fact]
    public async Task SubClaimFallback_IsUsedWhenNameMissing()
    {
        // Manually compose a host with sub-only auth (no DevAuth) so the GetTriggeredByOrNull
        // fallback to the 'sub' claim is exercised.
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<TriggeredByCapturingJob>().Named(JobName);
        });
        builder.Services.AddSingleton<CapturedTriggeredBy>();
        builder.Services
            .AddAuthentication(SubOnlyAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SubOnlyAuthHandler>(
                SubOnlyAuthHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/trigger/{jobName}",
            async (UKBatch.Runtime.IJobRunner runner, UKBatch.AspNetCore.Triggering.IJobTriggerContext idCtx, UKBatch.AspNetCore.Triggering.IJobTraceContext traceCtx, string jobName, CancellationToken ct) =>
            {
                var execution = await runner.TriggerWithRequestContextAsync(
                    idCtx, traceCtx, jobName, UKBatch.Abstractions.Jobs.JobParameters.Empty, ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    execution.ExecutionId,
                    execution.TriggeredBy,
                    Status = execution.Status.ToString(),
                });
            });
        try
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Sub", "user-42");

            var response = await client.GetAsync(new Uri($"/trigger/{JobName}", UriKind.Relative));
            var body = await response.ShouldBeAsync(HttpStatusCode.OK);
            body.Should().Contain("\"triggeredBy\":\"user-42\"");

            var captured = await app.Services
                .GetRequiredService<CapturedTriggeredBy>()
                .WaitAsync(TimeSpan.FromSeconds(60));
            captured.Should().Be("user-42");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

/// <summary>Test-only auth handler that emits only a <c>sub</c> claim — no ClaimTypes.Name.</summary>
internal sealed class SubOnlyAuthHandler
    : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
{
    public const string SchemeName = "SubOnly";

    public SubOnlyAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Sub", out var sub) || string.IsNullOrEmpty(sub))
        {
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        }
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", sub!),
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, Scheme.Name);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
    }
}
