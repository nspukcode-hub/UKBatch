using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.OpenIdConnect;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Integration;

/// <summary>
/// A no-op job the enforcement host can trigger and run through a batch.
/// </summary>
internal sealed class DemoJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Boots a real UKBatch REST host whose <c>/api</c> group is role-gated with
/// <c>RequireUKBatchRoleAuthorization()</c>, authenticated by JWT bearer against an in-memory symmetric
/// signing key (no live identity provider). This exercises the REAL JwtBearer handler, the REAL nested-role
/// flattening transformation, and the REAL role-gating convention against the REAL endpoints — the only
/// faithful way to prove that a viewer token cannot trigger a job and an operator token can.
/// </summary>
public sealed class RoleGatedApiHostFixture : IAsyncLifetime
{
    internal const string OperatorRole = "batch-operator";
    internal const string ViewerRole = "batch-viewer";
    internal const string GateRole = "finance-approver";
    internal const string PlainBatchName = "demo-pipeline";
    internal const string GatedBatchName = "gated-approval";
    internal const string ViewerPolicy = "UKBatch:Viewer";
    internal const string OperatorPolicy = "UKBatch:Operator";

    // A 256-bit key for HMAC-SHA256. Test-only; never a shipped secret.
    internal static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("ukbatch-oidc-self-issued-jwt-tests-signing-key-0123456789"));

    internal static readonly string DemoJobName = typeof(DemoJob).FullName!;

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment("Development");
            web.ConfigureServices(ConfigureServices);
            web.Configure(Configure);
        });
        _host = await builder.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>A fresh client per call (each test sets its own bearer).</summary>
    public HttpClient CreateClient() => _host.GetTestServer().CreateClient();

    /// <summary>A client that carries the given bearer token on every request.</summary>
    public HttpClient CreateClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Shared by the chained-authorization fixture so both hosts run the same runtime, JWT validation,
    /// flattening, and policy pair — the only difference between them is how the group is gated.
    /// </summary>
    internal static void ConfigureServices(IServiceCollection services)
    {
        services.AddUKBatchAspNetCore(b =>
        {
            b.AddJob<DemoJob>();
            b.AddBatch(PlainBatchName, batch => batch.RunJob<DemoJob>());
            b.AddBatch(GatedBatchName, batch => batch
                .RunJob<DemoJob>()
                .ThenWaitForApproval(
                    title: "Confirm rollout",
                    roles: new[] { GateRole },
                    timeout: TimeSpan.FromMinutes(10),
                    onTimeout: ApprovalTimeoutAction.Hold));
        });
        services.AddUKBatchApi();

        // The REAL nested-role flattening transformation, with the default Keycloak role-claim paths.
        services.AddOptions<UKBatchOpenIdConnectOptions>().Configure(o =>
        {
            o.OperatorRoles = new List<string> { OperatorRole };
        });
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, KeycloakRoleFlatteningTransformation>();

        // JWT bearer validated against the in-memory key — no metadata discovery, no live provider.
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = SigningKey,
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = "preferred_username",
                };
            });

        // The viewer/operator policy pair the convention maps endpoints to. Operator evaluates the operator
        // role at request time (mirrors the production RequireAssertion so an empty snapshot can't slip in).
        var operatorRoles = new[] { OperatorRole };
        services.AddAuthorizationBuilder()
            .AddPolicy(ViewerPolicy, p => p.RequireAuthenticatedUser())
            .AddPolicy(OperatorPolicy, p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => operatorRoles.Any(ctx.User.IsInRole)));
    }

    private static void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints =>
            endpoints.MapGroup("/api").MapUKBatchApi().RequireUKBatchRoleAuthorization());
    }

    // ---- Token minting ----------------------------------------------------------------------------

    /// <summary>Mints a token carrying flat <see cref="ClaimTypes.Role"/> claims (no nested realm shape).</summary>
    internal static string TokenWithFlatRoles(string user, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", user + "-subject"),
            new("preferred_username", user),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return Mint(claims);
    }

    /// <summary>
    /// Mints a token whose roles live ONLY under <c>realm_access.roles</c> (a real Keycloak shape) with no
    /// flat role claim, so only the flattening transformation can feed the policy.
    /// </summary>
    internal static string TokenWithNestedRealmRoles(string user, params string[] roles)
    {
        var realmAccess = JsonSerializer.Serialize(new { roles });
        var claims = new List<Claim>
        {
            new("sub", user + "-subject"),
            new("preferred_username", user),
            new("realm_access", realmAccess, JsonClaimValueTypes.Json),
        };

        return Mint(claims);
    }

    private static string Mint(IEnumerable<Claim> claims)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
