using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;
using UKBatch.Server.DevAuth;
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

// Auth core services. By default NO scheme is registered → the auth/authorization middleware below is a
// genuine no-op for every request (unchanged production posture). These are REQUIRED in DI before
// app.UseAuthentication()/app.UseAuthorization(): those middlewares activate IAuthenticationSchemeProvider
// / IAuthorizationPolicyProvider at pipeline-build time and throw if absent — they are NOT no-ops when
// the services are missing. Registering them here keeps the middleware in the pipeline (present so a
// deployment can add a scheme) while no-op'ing until a scheme is configured (mirrors how
// Sample.Dashboard / Sample.RestApi register AddAuthentication + AddAuthorization before Use*).
//
// OPT-IN DevAuth: the demo's approval gate is allowedRoles:["ops"]; with
// no scheme EVERY request is anonymous and NO approval config is anonymous-satisfiable ([]→500, ["ops"]→403,
// ["*"]→403 — the wildcard sentinel still rejects anonymous callers). When UKBATCH_DEV_AUTH=true (set by
// docker-compose for the demo) we register the sample-local header-based DevAuth scheme so an operator can
// approve via curl with `X-Dev-User` + `X-Dev-Roles: ops`. Default false → no scheme → production untouched.
// Browser dashboard approve button has no header-injection/login flow → curl is the approval path (OIDC = v0.2).
var enableDevAuth = bool.TryParse(cfg["UKBATCH_DEV_AUTH"], out var da) && da;
if (enableDevAuth)
{
    builder.Services.AddAuthentication("DevAuth")
        .AddScheme<DevAuthSchemeOptions, DevAuthHandler>("DevAuth", _ => { });
}
else
{
    builder.Services.AddAuthentication();   // no scheme → no-op (unchanged production posture)
}
builder.Services.AddAuthorization();

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
                // returns 403. These DevAuth header names match DevAuthHandler (X-Dev-User / X-Dev-Roles)
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

// ── 6. Middleware + endpoints ────────────────────────────────────────────────────────────────────
app.UseAuthentication();   // no scheme registered by default → no-op; present so a deployment can add one
app.UseAuthorization();
if (enableDashboard)
{
    app.UseAntiforgery();   // REQUIRED — MapRazorComponents emits anti-forgery metadata; else /dashboard 500
}

app.MapGroup("/api").MapUKBatchApi();                       // REST + hub + /api/workers/*
if (transport is "http")
{
    app.MapUKBatchHttpTransport();                          // receiver endpoints (the orchestrator can also receive)
}

if (enableDashboard)
{
    app.MapUKBatchDashboard();
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
