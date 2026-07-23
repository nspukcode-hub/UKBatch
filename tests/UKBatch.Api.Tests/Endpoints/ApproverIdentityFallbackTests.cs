using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Storage;
using UKBatch.Api;
using UKBatch.AspNetCore;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>A no-op job used to build a gated batch.</summary>
internal sealed class NoopJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Test-only authentication that builds the principal from request headers, so a test can control
/// EXACTLY which identity claims are present (name / preferred_username / sub) — impossible with the
/// dev-auth helper, which always sets a display name.
/// </summary>
internal sealed class HeaderPrincipalAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "HeaderPrincipal";

    public HeaderPrincipalAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var name = Request.Headers["X-Approver-Name"].ToString();
        var preferredUsername = Request.Headers["X-Approver-PreferredUsername"].ToString();
        var subject = Request.Headers["X-Approver-Sub"].ToString();
        var roles = Request.Headers["X-Approver-Roles"].ToString();

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(preferredUsername) && string.IsNullOrEmpty(subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult()); // anonymous
        }

        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(name))
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }
        if (!string.IsNullOrEmpty(preferredUsername))
        {
            claims.Add(new Claim("preferred_username", preferredUsername));
        }
        if (!string.IsNullOrEmpty(subject))
        {
            claims.Add(new Claim("sub", subject));
        }
        foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        // AuthenticationType = scheme name → IsAuthenticated is true.
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Proves the approver-identity resolution order used when recording who decided a gate:
/// <c>Identity.Name → preferred_username → sub → "anonymous"</c>. A Keycloak principal without a mapped
/// display name would otherwise be recorded as "anonymous". The decided identity is read back from the
/// approval store.
/// </summary>
public sealed class ApproverIdentityFallbackTests : IClassFixture<ApproverIdentityFallbackTests.HostFixture>
{
    private const string GatedBatch = "approver-gate";
    private readonly HostFixture _host;

    public ApproverIdentityFallbackTests(HostFixture host) => _host = host;

    [Fact]
    public async Task DisplayName_WinsOverPreferredUsernameAndSub()
    {
        var decidedBy = await ApproveAndReadDecidedByAsync(
            name: "Alice Display", preferredUsername: "alice-pu", subject: "sub-123");
        decidedBy.Should().Be("Alice Display", "Identity.Name is the first choice when present");
    }

    [Fact]
    public async Task PreferredUsername_UsedWhenNoDisplayName()
    {
        var decidedBy = await ApproveAndReadDecidedByAsync(
            name: null, preferredUsername: "alice-pu", subject: "sub-123");
        decidedBy.Should().Be("alice-pu", "with no display name the standard OpenID Connect username is used");
    }

    [Fact]
    public async Task Subject_UsedWhenNoNameNorPreferredUsername()
    {
        var decidedBy = await ApproveAndReadDecidedByAsync(
            name: null, preferredUsername: null, subject: "sub-123");
        decidedBy.Should().Be("sub-123", "the subject id is the last resort before the anonymous sentinel");
    }

    private async Task<string?> ApproveAndReadDecidedByAsync(string? name, string? preferredUsername, string? subject)
    {
        // Trigger the gated batch (anonymous trigger is fine — /api is not role-gated here).
        using var trigger = _host.CreateClient();
        var runResp = await trigger.PostAsync(
            new Uri($"/api/batches/by-name/{GatedBatch}/run", UriKind.Relative),
            new StringContent("{}", Encoding.UTF8, "application/json"));
        runResp.EnsureSuccessStatusCode();
        var batchId = JsonDocument.Parse(await runResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("batchId").GetString()!;

        var approvalId = await PollForApprovalAsync(batchId);

        // Approve as a principal carrying the gate role but with the chosen identity-claim shape.
        using var approver = _host.CreateClient();
        if (!string.IsNullOrEmpty(name))
        {
            approver.DefaultRequestHeaders.TryAddWithoutValidation("X-Approver-Name", name);
        }
        if (!string.IsNullOrEmpty(preferredUsername))
        {
            approver.DefaultRequestHeaders.TryAddWithoutValidation("X-Approver-PreferredUsername", preferredUsername);
        }
        if (!string.IsNullOrEmpty(subject))
        {
            approver.DefaultRequestHeaders.TryAddWithoutValidation("X-Approver-Sub", subject);
        }
        approver.DefaultRequestHeaders.TryAddWithoutValidation("X-Approver-Roles", "approver");

        var approveResp = await approver.PostAsync(
            new Uri($"/api/approvals/{approvalId}/approve", UriKind.Relative),
            new StringContent("{\"note\":\"ok\"}", Encoding.UTF8, "application/json"));
        approveResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Approve resolves synchronously, but the store's decided-write runs on the batch's gate-await
        // continuation, so poll until the record is Decided before reading who decided it.
        var store = _host.Services.GetRequiredService<IApprovalGateStore>();
        for (var i = 0; i < 200; i++)
        {
            var record = await store.GetAsync(approvalId, CancellationToken.None);
            if (record is { Status: ApprovalRecordStatus.Decided })
            {
                return record.DecidedBy;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Approval {approvalId} was not recorded as decided in time.");
    }

    private async Task<string> PollForApprovalAsync(string batchId)
    {
        using var client = _host.CreateClient();
        for (var i = 0; i < 200; i++)
        {
            var resp = await client.GetAsync(new Uri("/api/approvals", UriKind.Relative));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (item.GetProperty("batchId").GetString() == batchId)
                    {
                        return item.GetProperty("approvalId").GetString()!;
                    }
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No pending approval surfaced for batch {batchId}.");
    }

    /// <summary>Self-contained host: a gated batch + a header-driven auth scheme, no role-gating on /api.</summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        private static readonly string[] GateRoles = { "approver" };
        private IHost _host = null!;

        public IServiceProvider Services => _host.Services;

        public HttpClient CreateClient() => _host.GetTestServer().CreateClient();

        public async Task InitializeAsync()
        {
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.UseEnvironment("Development");
                web.ConfigureServices(services =>
                {
                    services.AddUKBatch(b =>
                    {
                        b.AddJob<NoopJob>();
                        b.AddBatch(GatedBatch, batch => batch
                            .RunJob<NoopJob>()
                            .ThenWaitForApproval(
                                title: "Confirm",
                                roles: GateRoles,
                                timeout: TimeSpan.FromMinutes(10),
                                onTimeout: ApprovalTimeoutAction.Hold));
                    });
                    services.AddUKBatchAspNetCore();
                    services.AddUKBatchApi();
                    services
                        .AddAuthentication(HeaderPrincipalAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HeaderPrincipalAuthHandler>(
                            HeaderPrincipalAuthHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGroup("/api").MapUKBatchApi());
                });
            });
            _host = await builder.StartAsync();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
