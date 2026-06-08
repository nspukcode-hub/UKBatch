using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.DevAuth;
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

if (!allowAnonymous && !enableDevAuth)
{
    throw new InvalidOperationException(
        "UKBatch.Server refuses to start without an explicit auth posture. This server has no " +
        "production authentication scheme in this release and would otherwise expose trigger, cancel, " +
        "delete, worker-registration and SignalR endpoints anonymously. Choose ONE:\n" +
        "  • Set UKBATCH_ALLOW_ANONYMOUS=true to run anonymously ONLY behind a trusted network or an " +
        "external auth gateway (reverse proxy / API gateway that authenticates callers).\n" +
        "  • Set UKBATCH_DEV_AUTH=true for demos — registers a header-trusting dev scheme (NOT secure; " +
        "callers self-assert identity via X-Dev-User / X-Dev-Roles).\n" +
        "  • Wait for the OIDC support planned for a future release.");
}

if (enableDevAuth)
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
