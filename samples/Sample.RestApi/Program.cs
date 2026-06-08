using Sample.RestApi.Jobs;
using UKBatch.Abstractions.Batches;
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.DevAuth;
using UKBatch.Storage.EntityFrameworkCore;

string[] OpsRoles = { "ops" };
string[] WildcardRoles = { ApprovalGateConfig.AnyAuthenticatedUser };
var builder = WebApplication.CreateBuilder(args);
const string InvoicePipelineName = "invoice-pipeline";
const string WildcardApprovalPipelineName = "wildcard-approval-pipeline";

var approvalTimeoutSeconds = builder.Configuration.GetValue<int>("Sample:ApprovalTimeoutSeconds", 5);

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
    // Fixture — a partitioned job for pagination tests.
    b.AddPartitionedJob<BulkArchiveJob, string>();
    b.AddBatch(InvoicePipelineName, batch => batch
        .RunJob<InvoiceGenerationJob>()
        .ThenInParallel(p => p
            .RunJob<EmailNotificationJob>()
            .RunJob<ArchiveJob>()
            .JoinPolicy(ParallelJoinPolicy.WaitAll))
        .ThenWaitForApproval(
            title: "Confirm rollout",
            roles: OpsRoles,
            timeout: TimeSpan.FromSeconds(approvalTimeoutSeconds),
            onTimeout: ApprovalTimeoutAction.AutoApprove)
        .OnFailure(f => f.RunJob<RollbackJob>())
        .FailurePolicy(BatchFailurePolicy.Compensate));

    // Fixture: an approval gate configured with the AnyAuthenticatedUser
    // wildcard sentinel. Used by ApprovalsEndpointTests.Approve_AnonymousWithWildcardConfig_Returns403
    // to lock the security rule that prevents anonymous callers from satisfying ["*"].
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

// ---- Pluggable storage (--storage inmemory | ef-sqlite | ef-pg) ----
// Default is the in-memory store — UNCHANGED behavior; the WebApplicationFactory tests rely on it.
// ef-sqlite / ef-pg swap in the EF Core adapter with NO change to any job/batch wiring above — the
// "swap storage, code unchanged" promise. The EF stores REPLACE the in-memory descriptors registered by
// AddUKBatchAspNetCore (DI RemoveAll + re-add), so this MUST run AFTER AddUKBatchApi.
//   --storage ef-sqlite [--storage-connection "DataSource=ukbatch-sample.db"]
//   --storage ef-pg     --storage-connection "Host=localhost;Database=ukbatch;Username=...;Password=..."
var storage = (builder.Configuration["storage"] ?? "inmemory").ToLowerInvariant();
var storageConnection = builder.Configuration["storage-connection"];
switch (storage)
{
    case "inmemory":
        break;   // Core in-memory stores (default).
    case "ef-sqlite":
        builder.Services.AddUKBatchEntityFrameworkCoreStores(o =>
        {
            o.UseSqlite(storageConnection ?? "DataSource=ukbatch-sample.db");
            o.MigrateOnStartup = true;   // dev convenience: create/upgrade schema on boot
        });
        break;
    case "ef-pg":
    case "ef-postgres":
        builder.Services.AddUKBatchEntityFrameworkCoreStores(o =>
        {
            o.UsePostgres(storageConnection ?? throw new InvalidOperationException(
                "--storage ef-pg requires --storage-connection \"Host=...;Database=...;Username=...;Password=...\""));
            o.MigrateOnStartup = true;
        });
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown --storage value '{storage}'. Valid: inmemory | ef-sqlite | ef-pg.");
}

// DEVELOPMENT ONLY — header-trusting dev auth (X-Dev-User / X-Dev-Roles). Refused in Production.
builder.Services.AddUKBatchDevAuth();

var app = builder.Build();
app.Logger.LogInformation("UKBatch sample storage provider: {Storage}", storage);
app.UseAuthentication();
app.UseAuthorization();

// MAP the API surface — auth optional via RequireAuthorization on the group.
// Endpoints are registered with global WithName(...) ids for OpenAPI operation linking.
// Dual-mount demonstration. The parameterless MapUKBatchApi mounts the
// anonymous surface AND the SignalR hub. The "Secured" overload mounts a SECOND copy of the
// REST surface under /api/secured with operation-id prefix to avoid OpenAPI collisions; auth
// is enforced via RequireAuthorization (DevAuth scheme in this sample).
app.MapGroup("/api").MapUKBatchApi();
app.MapGroup("/api/secured")
    .MapUKBatchApi("Secured")
    .RequireAuthorization();

#if NET10_0_OR_GREATER
app.MapOpenApi();
#endif
app.MapHealthChecks("/healthz");

app.Run();

namespace Sample.RestApi
{
    /// <summary>Marker for WebApplicationFactory test discovery.</summary>
    public partial class Program;
}
