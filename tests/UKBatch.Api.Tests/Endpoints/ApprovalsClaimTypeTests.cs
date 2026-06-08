using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using UKBatch;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// endpoint tests for
/// <see cref="UKBatchOptions.ApprovalRoleClaimTypes"/> configurability. Custom claim type schemes
/// (e.g. IdentityServer's "role") satisfy AllowedRoles when configured.
/// </summary>
public sealed class ApprovalsClaimTypeTests
{
    /// <summary>Factory variant that configures <c>ApprovalRoleClaimTypes</c>.</summary>
    private sealed class CustomClaimTypesFactory(IReadOnlyList<string> claimTypes) : WebApplicationFactory<Sample.RestApi.Program>
    {
        public CustomClaimTypesFactory() : this(new[] { ClaimTypes.Role }) { }

        public CustomClaimTypesFactory(params string[] types) : this((IReadOnlyList<string>)types) { }

        // Approval timeout short for test responsiveness (Sample.RestApi reads this env var on startup).
        static CustomClaimTypesFactory() => Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<UKBatchOptions>(o =>
                {
                    o.ApprovalRoleClaimTypes = new List<string>(claimTypes);
                });

                // The packaged dev-auth handler emits roles only under ClaimTypes.Role. These tests
                // exercise ApprovalRoleClaimTypes against a CUSTOM claim type (e.g. "role" for
                // IdentityServer-style identities), so a test-only claims transformation reads the
                // optional X-Dev-Custom-Role-Type / X-Dev-Custom-Roles headers and adds role claims
                // under the named custom type. This lives in the test host, never in the shipped helper.
                services.AddSingleton<IClaimsTransformation, CustomRoleHeaderTransformation>();
            });
        }
    }

    /// <summary>
    /// Test-only claims transformation that mirrors the optional custom-claim-type behavior the
    /// approval claim-type tests rely on: it reads <c>X-Dev-Custom-Role-Type</c> +
    /// <c>X-Dev-Custom-Roles</c> from the current request and appends role claims under the named
    /// custom claim type. Production identities never set these headers.
    /// </summary>
    private sealed class CustomRoleHeaderTransformation(IHttpContextAccessor httpContextAccessor)
        : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var request = httpContextAccessor.HttpContext?.Request;
            if (request is null
                || principal.Identity is not ClaimsIdentity identity
                || !identity.IsAuthenticated)
            {
                return Task.FromResult(principal);
            }

            if (request.Headers.TryGetValue("X-Dev-Custom-Role-Type", out var customType)
                && !string.IsNullOrEmpty(customType.ToString())
                && request.Headers.TryGetValue("X-Dev-Custom-Roles", out var customRoles))
            {
                var type = customType.ToString();
                foreach (var role in customRoles.ToString()
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // Avoid duplicating a claim that an earlier transformation pass already added
                    // (IClaimsTransformation can run more than once per principal).
                    if (!identity.HasClaim(type, role))
                    {
                        identity.AddClaim(new Claim(type, role));
                    }
                }
            }

            return Task.FromResult(principal);
        }
    }

    [Fact]
    public async Task Approve_CustomClaimType_Succeeds()
    {
        // Configure custom claim type "role" (IdentityServer-style). DevAuth emits an "ops" claim
        // under the "role" custom type via X-Dev-Custom-Role-Type/X-Dev-Custom-Roles headers.
        // The endpoint's BuildApproverFromHttpContext must pick up the role under the configured
        // claim type → approve succeeds (204).
        await using var factory = new CustomClaimTypesFactory("role");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-User", "alice");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Custom-Role-Type", "role");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Custom-Roles", "ops");

        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");
        var approvalId = await client.PollForPendingApprovalAsync(batchId);
        var resp = await client.PostAsync(
            new Uri($"/api/approvals/{approvalId}/approve", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { note = "ok" }));
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "custom claim type configured: 'ops' under 'role' claim type satisfies AllowedRoles=['ops'].");
    }

    [Fact]
    public async Task Approve_DefaultBehaviorPreserved_WithStandardClaimType()
    {
        // Out-of-the-box default ApprovalRoleClaimTypes = [ClaimTypes.Role]. DevAuth emits 'ops'
        // under ClaimTypes.Role via X-Dev-Roles → approve succeeds.
        await using var factory = new SampleRestApiFactory();
        using var client = factory.CreateClient().WithDevAuth("alice", "ops");
        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");
        var approvalId = await client.PollForPendingApprovalAsync(batchId);
        var resp = await client.PostAsync(
            new Uri($"/api/approvals/{approvalId}/approve", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { note = "ok" }));
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
 " hardcoded behavior must continue to work under default ApprovalRoleClaimTypes.");
    }

    [Fact]
    public async Task Approve_MultipleClaimTypes_DedupesValues()
    {
        // Configure BOTH ClaimTypes.Role AND "role". DevAuth emits 'ops' under both. The
        // BuildApproverFromHttpContext must dedupe to a SINGLE 'ops' entry. We can't directly
        // observe the ApproverContext.Roles count via the endpoint, but the endpoint contract
        // is "if any configured claim type contributes a matching role, approval succeeds".
        // Verify by approving an "ops"-gated batch with both types configured.
        await using var factory = new CustomClaimTypesFactory(ClaimTypes.Role, "role");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-User", "alice");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Roles", "ops");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Custom-Role-Type", "role");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Custom-Roles", "ops");

        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");
        var approvalId = await client.PollForPendingApprovalAsync(batchId);
        var resp = await client.PostAsync(
            new Uri($"/api/approvals/{approvalId}/approve", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { note = "ok" }));
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "multiple claim types: dedupe still admits role 'ops' from both sources.");
    }

    [Fact]
    public async Task Approve_AnonymousFallback_NoRoles_Returns403()
    {
        // Configured to ONLY "role" (custom). Anonymous request emits no claims under any type.
        // Approving an "ops"-gated batch → 403.
        await using var factory = new CustomClaimTypesFactory("role");
        using var client = factory.CreateClient();
        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");
        var approvalId = await client.PollForPendingApprovalAsync(batchId);
        var resp = await client.PostAsync(
            new Uri($"/api/approvals/{approvalId}/approve", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { note = "anon" }));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "anonymous fallback with no roles under configured claim type must be rejected.");
    }
}
