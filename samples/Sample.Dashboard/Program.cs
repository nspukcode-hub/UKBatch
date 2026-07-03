using Sample.Dashboard.Jobs;
using UKBatch.Abstractions.Batches;
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.DevAuth;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;

string[] OpsRoles = { "ops" };
string[] WildcardRoles = { ApprovalGateConfig.AnyAuthenticatedUser };
var builder = WebApplication.CreateBuilder(args);
const string InvoicePipelineName = "invoice-pipeline";
const string WildcardApprovalPipelineName = "wildcard-approval-pipeline";
const string OrderPipelineName = "order-pipeline";

// Embedded mode: same host hosts both the REST API surface AND the Blazor
// Dashboard. Dashboard talks to the local Api via HTTP/SignalR loopback. Architecturally
// identical to the server + workers deployment (only the BaseUrl differs).
builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o =>
    {
        o.MaxDegreeOfParallelism = 4;
        o.DispatcherChannelCapacity = 256;
        o.HubBufferCapacity = 256;
    });
    b.AddJob<InvoiceGenerationJob>();
    b.AddJob<EmailNotificationJob>();
    b.AddJob<ArchiveJob>();
    b.AddJob<RollbackJob>();
    b.AddPartitionedJob<BulkArchiveJob, string>();
    b.AddJob<PrepareOrderJob>();
    b.AddJob<ProcessInvoiceJob>();
    b.AddJob<FinalizeOrderJob>();
    b.AddBatch(InvoicePipelineName, batch => batch
        .RunJob<InvoiceGenerationJob>()
        .ThenInParallel(p => p
            .RunJob<EmailNotificationJob>()
            .RunJob<ArchiveJob>()
            .JoinPolicy(ParallelJoinPolicy.WaitAll))
        .ThenWaitForApproval(
            title: "Confirm rollout",
            roles: OpsRoles,
            // Manual approval: HOLD at the gate until an operator approves from the dashboard (no
            // auto-approve). 5-minute safety timeout. Approve via /dashboard/self/approvals.
            timeout: TimeSpan.FromMinutes(5),
            onTimeout: ApprovalTimeoutAction.Hold)
        .OnFailure(f => f.RunJob<RollbackJob>())
        .FailurePolicy(BatchFailurePolicy.Compensate));

    // Step-output forwarding demo: each step records output via context.Outputs.Set(...), and the next
    // step reads it from its parameters. PrepareOrder produces orderId (scalar) + order (object);
    // ProcessInvoice reads both and produces invoiceId; FinalizeOrder reads orderId + invoiceId — proving
    // outputs accumulate forward across the whole run. Watch the console logs, or open each execution in
    // the dashboard: its "Input parameters" panel shows what it received, its "Outputs" what it produced.
    b.AddBatch(OrderPipelineName, batch => batch
        .RunJob<PrepareOrderJob>()
        .ThenRunJob<ProcessInvoiceJob>()
        .ThenRunJob<FinalizeOrderJob>()
        .FailurePolicy(BatchFailurePolicy.StopOnFailure));

    // Wildcard approval pipeline — exercises [ApprovalGateConfig.AnyAuthenticatedUser] sentinel.
    b.AddBatch(WildcardApprovalPipelineName, batch => batch
        .RunJob<InvoiceGenerationJob>()
        .ThenWaitForApproval(
            title: "Any-auth approval",
            roles: WildcardRoles,
            timeout: TimeSpan.FromMinutes(5),
            onTimeout: ApprovalTimeoutAction.Hold)
        .FailurePolicy(BatchFailurePolicy.StopOnFailure));
});

builder.Services.AddUKBatchApi();

// Embedded dashboard. The `self` service descriptor points at the local
// REST API surface (mounted under `/api` below). BaseUrl is bound from appsettings.json by default
// (`UKBatch:Dashboard:Services[]`); the `configure` callback merges an in-code descriptor as a
// safety net so the sample boots even without an appsettings entry.
builder.Services.AddUKBatchDashboard(opts =>
{
    // Idempotent — if appsettings already bound a "self" descriptor, do not double-register.
    if (!opts.Services.Any(s => string.Equals(s.Name, "self", StringComparison.Ordinal)))
    {
        opts.Services.Add(new UKBatchServiceDescriptor
        {
            // BaseUrl MUST end with a trailing slash so HttpClient resolves relative paths under
            // the `/api` route group. Without the slash, `HttpClient` strips the last segment per
            // RFC 3986 and `jobs` resolves to `http://localhost:5057/jobs` → 404.
            // Port 5057 (NOT 5000): macOS Control Center / AirPlay Receiver listens on :5000 and
            // answers every HTTP request with `403 Forbidden` (Server: AirTunes/*). The dashboard
            // client would otherwise hit AirPlay as a phantom SignalR negotiate 403.
            Name = "self",
            BaseUrl = new Uri("http://localhost:5057/api/"),
            DisplayName = "Local",
            // DEMO-ONLY: the dashboard's server-side REST/SignalR calls to /api carry these DevAuth
            // headers so the Approve/Reject buttons actually authenticate (the gate roles are "ops" /
            // AnyAuthenticatedUser). Without an identity the approval endpoints reject anonymous callers.
            // A real deployment wires OIDC/Cookie auth instead of static headers.
            Headers = new Dictionary<string, string>
            {
                ["X-Dev-User"] = "operator",
                ["X-Dev-Roles"] = "ops",
            },
        });
    }
});

// DEVELOPMENT ONLY — header-trusting dev auth (X-Dev-User / X-Dev-Roles). The approval gate roles
// claim is "ops". Refused in Production.
builder.Services.AddUKBatchDevAuth();
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
// Required for Razor Components (MapRazorComponents emits anti-forgery metadata).
app.UseAntiforgery();



// Mount the REST API under `/api` and the SignalR hub under `/api/hubs/jobs`.
app.MapGroup("/api").MapUKBatchApi();

// Dashboard at literal `/dashboard/...` routes. Returns the convention builder so
// production deployments can chain `.RequireAuthorization()` to lock it down. The
// `Sample:Dashboard:RequireAuthorization` config flag (env `Sample__Dashboard__RequireAuthorization=true`)
// is the integration-test seam used by DashboardAuthOnTests to verify the auth-on
// wiring.
var dashboard = app.MapUKBatchDashboard();

if (app.Configuration.GetValue<bool>("Sample:Dashboard:RequireAuthorization"))
{
    dashboard.RequireAuthorization();
}

// MapStaticAssets — .NET 9/10 Blazor Web App convention. Mounts `_framework/blazor.web.js`
// + static web assets manifest. UseStaticFiles alone does NOT serve Blazor framework files.
// On net8.0 MapStaticAssets is unavailable; UseStaticFiles serves the static web assets instead.
#if NET10_0_OR_GREATER
app.MapStaticAssets();
#else
app.UseStaticFiles();
#endif

#if NET10_0_OR_GREATER
app.MapOpenApi();
#endif
app.MapHealthChecks("/healthz");

app.Run();

namespace Sample.Dashboard
{
    /// <summary>Marker for WebApplicationFactory test discovery.</summary>
    public partial class Program;
}
