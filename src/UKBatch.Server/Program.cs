using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.DevAuth;
using UKBatch.AspNetCore.OpenIdConnect;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;
using UKBatch.Storage.EntityFrameworkCore;
using UKBatch.Transport.Http;
using UKBatch.Transport.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ── 1. Core runtime (NO jobs/batches — the server is a pure orchestrator + dashboard + worker-
//       registry shell; batch definitions come from the persistent store via the dashboard wizard) ──
builder.AddUKBatchAspNetCore(b => b.Configure(o =>
{
    // Identity resolution: the flat UKBATCH_SERVICE_NAME env var is the canonical
    // operator knob and WINS, then the structured UKBatch:ThisServiceName key, then "ukbatch-server".
    // (Flat-first because appsettings would otherwise provide a non-null default that shadows the env
    // override — see the storage/transport switches below for the same precedence rule.)
    o.ThisServiceName = cfg["UKBATCH_SERVICE_NAME"] ?? cfg["UKBatch:ThisServiceName"] ?? "ukbatch-server";
    o.MaxDegreeOfParallelism = cfg.GetValue("UKBatch:MaxDegreeOfParallelism", Environment.ProcessorCount);
}));

// ── 2. REST API + SignalR hub + IWorkerRegistry (AddUKBatchApi registers the registry singleton) ──
builder.Services.AddUKBatchApi();

// ── Auth posture (FAIL-CLOSED) ──────────────────────────────────────────────────────────────────
// The server exposes trigger / cancel / delete / worker-beat / SignalR. This release ships NO production
// authentication scheme, so the operator MUST consciously choose a posture. Flat env var wins (canonical
// operator knob), then the structured key.
var allowAnonymous = (bool.TryParse(cfg["UKBATCH_ALLOW_ANONYMOUS"], out var aa) && aa)
    || (bool.TryParse(cfg["UKBatch:AllowAnonymous"], out var aaS) && aaS);
var enableDevAuth = (bool.TryParse(cfg["UKBATCH_DEV_AUTH"], out var da) && da)
    || (bool.TryParse(cfg["UKBatch:DevAuth"], out var daS) && daS);
// OIDC posture: setting an authority is the opt-in signal. Flat env var wins, then the structured key.
var oidcAuthority = cfg["UKBATCH_OIDC_AUTHORITY"] ?? cfg["UKBatch:Oidc:Authority"];
var enableOidc = !string.IsNullOrWhiteSpace(oidcAuthority);

if (!allowAnonymous && !enableDevAuth && !enableOidc)
{
    throw new InvalidOperationException(
        "UKBatch.Server refuses to start without an explicit auth posture. This server would otherwise " +
        "expose trigger, cancel, delete, worker-registration and SignalR endpoints anonymously. Choose ONE:\n" +
        "  • Set UKBATCH_OIDC_AUTHORITY (with client id/secret) to require OpenID Connect login with " +
        "viewer/operator role-gating — the production posture.\n" +
        "  • Set UKBATCH_ALLOW_ANONYMOUS=true to run anonymously ONLY behind a trusted network or an " +
        "external auth gateway (reverse proxy / API gateway that authenticates callers).\n" +
        "  • Set UKBATCH_DEV_AUTH=true for demos — registers a header-trusting dev scheme (NOT secure; " +
        "callers self-assert identity via X-Dev-User / X-Dev-Roles).");
}

if (enableOidc)
{
    // OpenID Connect (e.g. Keycloak): interactive cookie login for the dashboard + JWT bearer for the
    // API, with viewer/operator role-gating. The authority discovers its endpoints from
    // {authority}/.well-known/openid-configuration; no identity-provider-specific code lives here.
    builder.Services.AddUKBatchOpenIdConnect(o =>
    {
        o.Authority = oidcAuthority;
        o.ClientId = cfg["UKBATCH_OIDC_CLIENT_ID"] ?? cfg["UKBatch:Oidc:ClientId"];
        o.ClientSecret = cfg["UKBATCH_OIDC_CLIENT_SECRET"] ?? cfg["UKBatch:Oidc:ClientSecret"];
        o.Audience = cfg["UKBATCH_OIDC_AUDIENCE"] ?? cfg["UKBatch:Oidc:Audience"];
        var operatorRoles = cfg["UKBATCH_OIDC_OPERATOR_ROLES"] ?? cfg["UKBatch:Oidc:OperatorRoles"];
        if (!string.IsNullOrWhiteSpace(operatorRoles))
        {
            o.OperatorRoles = operatorRoles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        if (bool.TryParse(cfg["UKBATCH_OIDC_REQUIRE_HTTPS_METADATA"], out var requireHttps))
        {
            o.RequireHttpsMetadata = requireHttps;
        }
    });
}
else if (enableDevAuth)
{
    // Header-trusting dev scheme: an operator can approve via curl with `X-Dev-User` + `X-Dev-Roles: ops`.
    // Callers self-assert identity with no verification — demos only. The helper registers the scheme +
    // authorization and a startup guard that logs a loud warning. The server's own fail-closed posture
    // gate above already forced the operator to consciously set UKBATCH_DEV_AUTH=true, so allow it even
    // when the container runs in the Production environment (the operator opted in explicitly).
    builder.Services.AddUKBatchDevAuth(o => o.AllowInProduction = true);
}
else
{
    // allowAnonymous == true here. No scheme → the auth/authorization middleware is a genuine no-op;
    // the operator has explicitly accepted anonymous access behind their own gateway.
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization();
}

// ── 3. Storage (UKBATCH_STORAGE) — MUST be AFTER AddUKBatchApi: the EF adapter RemoveAll's the
//       in-memory store descriptors then re-adds the EF-backed singletons ──
// Precedence: the flat UKBATCH_STORAGE env var WINS (canonical operator knob, used
// by docker-compose), then the structured UKBatch:Storage:Provider key, then "inmemory". Flat-first
// because a structured appsettings default would otherwise shadow the env override.
var storage = (cfg["UKBATCH_STORAGE"] ?? cfg["UKBatch:Storage:Provider"] ?? "inmemory").ToLowerInvariant();
var storageConn = cfg["UKBATCH_STORAGE_CONNECTION"] ?? cfg["UKBatch:Storage:ConnectionString"];
switch (storage)
{
    case "inmemory":
        break;   // default — non-durable, demo / single-process
    case "ef-sqlite":
        builder.Services.AddUKBatchEntityFrameworkCoreStores(o =>
        {
            o.UseSqlite(storageConn ?? "DataSource=/data/ukbatch.db");
            o.MigrateOnStartup = true;
        });
        break;
    case "ef-pg":
    case "ef-postgres":
        builder.Services.AddUKBatchEntityFrameworkCoreStores(o =>
        {
            o.UsePostgres(storageConn ?? throw new InvalidOperationException(
                "ef-pg requires UKBATCH_STORAGE_CONNECTION (Host=...;Database=...;Username=...;Password=...)."));
            o.MigrateOnStartup = true;
        });
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown UKBATCH_STORAGE '{storage}'. Valid: inmemory | ef-sqlite | ef-pg.");
}

// ── 4. Transport (UKBATCH_TRANSPORT) — orchestrator side ─────────────────────────────────────────
// Flat UKBATCH_TRANSPORT env var WINS (canonical operator knob), then structured key, then "inprocess".
var transport = (cfg["UKBATCH_TRANSPORT"] ?? cfg["UKBatch:Transport:Provider"] ?? "inprocess").ToLowerInvariant();
switch (transport)
{
    case "inprocess":
        break;   // single-process / demo (no real cross-service fan-out)
    case "http":
        builder.Services.AddUKBatchHttpTransport();     // binds UKBatch:Transport:Http (Services dict, HMAC secret)
        break;
    case "rabbitmq":
    case "rabbit":
        builder.Services.AddUKBatchRabbitMqTransport();  // binds UKBatch:Transport:RabbitMQ
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown UKBATCH_TRANSPORT '{transport}'. Valid: inprocess | http | rabbitmq.");
}

// ── 5. Dashboard (UKBATCH_ENABLE_DASHBOARD, default true) + self-register loopback descriptor ─────
// Flat UKBATCH_ENABLE_DASHBOARD env var WINS (the Dockerfile + compose set this form), then the
// structured UKBatch:EnableDashboard key, then default true. Flat-first for the same precedence
// reason as storage/transport (an appsettings default would otherwise pin it true).
var enableDashboard = (bool.TryParse(cfg["UKBATCH_ENABLE_DASHBOARD"], out var flatDash)
        ? flatDash
        : cfg.GetValue<bool?>("UKBatch:EnableDashboard"))
    ?? true;
if (enableDashboard)
{
    builder.Services.AddUKBatchDashboard(opts =>
    {
        // Under the OIDC posture the dashboard forwards each signed-in user's token to the API rather
        // than a single static machine identity.
        opts.PerUserAuthentication = enableOidc;
        if (!opts.Services.Any(s => string.Equals(s.Name, "self", StringComparison.Ordinal)))
        {
            var selfBase = cfg["UKBatch:Dashboard:SelfBaseUrl"] ?? "http://localhost:8080/api/";
            // TRAILING-SLASH MANDATORY — HttpClient strips the last path segment per RFC 3986;
            // without the slash "jobs" resolves to /jobs → 404.
            if (!selfBase.EndsWith('/'))
            {
                selfBase += "/";
            }

            opts.Services.Add(new UKBatchServiceDescriptor
            {
                Name = "self",
                BaseUrl = new Uri(selfBase, UriKind.Absolute),
                DisplayName = "Server",
                // When DevAuth is on, the dashboard's own REST + hub calls (incl. the
                // Approve/Reject POSTs) must carry the ops identity, else the server sees anonymous and
                // returns 403. These header names match the dev-auth scheme (X-Dev-User / X-Dev-Roles)
                // and live ONLY here in the server host. Production (DevAuth off) → null → unchanged.
                Headers = enableDevAuth
                    ? new Dictionary<string, string>
                    {
                        ["X-Dev-User"] = "dashboard",
                        ["X-Dev-Roles"] = "ops",
                    }
                    : null,
            });
        }
    });
    builder.Services.AddAntiforgery();   // Razor Components requirement (else /dashboard 500)
}

var app = builder.Build();

// OIDC without an audience accepts any token the realm issued (issuer + signature only) — workable for
// a dedicated realm, but a shared realm should mint an API audience and set it here.
if (enableOidc && string.IsNullOrWhiteSpace(cfg["UKBATCH_OIDC_AUDIENCE"] ?? cfg["UKBatch:Oidc:Audience"]))
{
    app.Logger.LogWarning(
        "UKBATCH_OIDC_AUDIENCE is not set: bearer tokens are accepted on issuer and signature alone, so a " +
        "token minted for ANY application in this realm can call the API. In production, register an " +
        "audience with the identity provider (for Keycloak, an audience mapper) and set UKBATCH_OIDC_AUDIENCE.");
}

// Loud startup warning for the anonymous posture (the throw above guarantees a posture is set; the
// DevAuth helper logs its own warning via the startup guard it registers).
if (allowAnonymous && !enableDevAuth)
{
    app.Logger.LogWarning(
        "UKBatch.Server is running with UKBATCH_ALLOW_ANONYMOUS=true: ALL endpoints (trigger, cancel, " +
        "delete, worker registration, SignalR) are reachable WITHOUT authentication. This is safe ONLY " +
        "behind a trusted network or an external auth gateway. Do NOT expose this server directly to " +
        "untrusted networks.");
}

// ── 6. Middleware + endpoints ────────────────────────────────────────────────────────────────────
app.UseAuthentication();   // no-op under anonymous posture; activates the DevAuth scheme when enabled
app.UseAuthorization();
if (enableDashboard)
{
    app.UseAntiforgery();   // REQUIRED — MapRazorComponents emits anti-forgery metadata; else /dashboard 500
}

var apiGroup = app.MapGroup("/api").MapUKBatchApi();        // REST + hub + /api/workers/*
if (enableOidc)
{
    // Reads require a viewer, writes require an operator; approve/reject require an authenticated caller
    // (the gate's own allowed-roles is the finer authority). Untouched under the other postures.
    apiGroup.RequireUKBatchRoleAuthorization();
}
if (transport is "http")
{
    app.MapUKBatchHttpTransport();                          // receiver endpoints (the orchestrator can also receive)
}

if (enableDashboard)
{
    var dashboard = app.MapUKBatchDashboard();
    if (enableOidc)
    {
        dashboard.RequireAuthorization();
        // The dashboard is mounted at /dashboard, so land there after sign-out (the site root is empty);
        // /dashboard then re-challenges an unauthenticated user back to the provider's login.
        app.MapUKBatchSignOut(redirectUri: "/dashboard");
    }
    app.MapStaticAssets();   // Blazor framework assets (_framework/blazor.web.js); order replicates Sample.Dashboard
}

app.MapOpenApi();
app.MapHealthChecks("/healthz");

app.Run();

namespace UKBatch.Server
{
    /// <summary>Marker for WebApplicationFactory test discovery.</summary>
    public partial class Program;
}
