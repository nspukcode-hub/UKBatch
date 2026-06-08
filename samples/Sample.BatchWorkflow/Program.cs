using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Sample.BatchWorkflow.Jobs;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.DevAuth;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Runtime;

string[] OpsRoles = { "ops" };
var builder = WebApplication.CreateBuilder(args);
const string InvoicePipelineId = "invoice-pipeline";

// Approval timeout is configurable via Sample:ApprovalTimeoutSeconds (default 30 for manual runs).
var approvalTimeoutSeconds = builder.Configuration.GetValue<int>("Sample:ApprovalTimeoutSeconds", 30);

builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o =>
    {
        o.MaxDegreeOfParallelism = 4;
        o.DispatcherChannelCapacity = 256;
    });
    // Batch steps (BatchBuilder.RunJob<T>) reference jobs by typeof(T).FullName. Register the
    // jobs with their default FullName-based name so the lookup at execution time matches.
    b.AddJob<InvoiceGenerationJob>();
    b.AddJob<EmailNotificationJob>();
    b.AddJob<SmsNotificationJob>();
    b.AddJob<ArchiveJob>();
    b.AddJob<RollbackJob>();
    b.AddBatch(InvoicePipelineId, batch => batch
        .RunJob<InvoiceGenerationJob>()
        .ThenInParallel(p => p
            .RunJob<EmailNotificationJob>()
            .RunJob<SmsNotificationJob>()
            .JoinPolicy(ParallelJoinPolicy.WaitAll))
        .ThenWaitForApproval(
            title: "Confirm rollout",
            roles: OpsRoles,
            timeout: TimeSpan.FromSeconds(approvalTimeoutSeconds),
            onTimeout: ApprovalTimeoutAction.AutoApprove)
        .ThenRunJob<ArchiveJob>()
        .OnFailure(f => f.RunJob<RollbackJob>())
        .FailurePolicy(BatchFailurePolicy.Compensate));
});

// DEVELOPMENT ONLY — header-trusting dev auth (X-Dev-User / X-Dev-Roles). Refused in Production.
builder.Services.AddUKBatchDevAuth();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/healthz");

// Resolve the code-defined batch's real id by Name via the public
// IBatchDefinitionLookup contract. UKBatchBuilder.Complete() assigns each code-defined batch a
// fresh id via IdGenerator.NewBatchId() and stores the user-supplied "name" as
// BatchDefinition.Name. The lookup is registered as a DI singleton by Core.
var lookup = app.Services.GetRequiredService<IBatchDefinitionLookup>();
var pipelineRuntimeId = lookup.TryGetByName(InvoicePipelineId)?.Id
    ?? throw new InvalidOperationException(
        $"Batch definition with Name='{InvoicePipelineId}' was not found in the registry.");

app.MapPost("/batches/run",
    async (IJobRunner runner, IJobTriggerContext idCtx, IJobTraceContext traceCtx, CancellationToken ct) =>
    {
        var batchId = await runner.TriggerBatchWithRequestContextAsync(
            idCtx, traceCtx, batchDefinitionId: pipelineRuntimeId!, initialParameters: null, ct);
        return Results.Ok(new { batchId });
    });

app.MapGet("/batches/{id}/status",
    async (IJobExecutionReader reader, string id, CancellationToken ct) =>
    {
        // JobQuery.BatchId filter exists (Abstractions/Models/JobQuery.cs:13).
        var query = new JobQuery { BatchId = id, Limit = 100, Offset = 0 };
        var executions = await reader.QueryAsync(query, ct);
        return Results.Ok(new { batchId = id, executions });
    });

// [Authorize] attribute drives role enforcement; identity read from ClaimTypes.Name. The scheme name
// matches the dev-auth scheme registered by AddUKBatchDevAuth.
app.MapPost("/approvals/{id}/approve",
    [Authorize(AuthenticationSchemes = "DevAuth", Roles = "ops")]
    async (IApprovalGateService svc, string id, HttpContext http, CancellationToken ct) =>
    {
        var identity = http.User.Identity?.Name ?? "anonymous";
        var roles = http.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var approver = new ApproverContext { Identity = identity, Roles = roles };
        await svc.ApproveAsync(id, approver, note: null, ct);
        return Results.Ok();
    });

app.MapPost("/approvals/{id}/reject",
    [Authorize(AuthenticationSchemes = "DevAuth", Roles = "ops")]
    async (IApprovalGateService svc, string id, string reason, HttpContext http, CancellationToken ct) =>
    {
        var identity = http.User.Identity?.Name ?? "anonymous";
        var roles = http.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var approver = new ApproverContext { Identity = identity, Roles = roles };
        await svc.RejectAsync(id, approver, reason, ct);
        return Results.Ok();
    });

app.MapGet("/approvals",
    async (IApprovalGateService svc, string? role, CancellationToken ct) =>
    {
        var pending = await svc.ListPendingAsync(role, ct);
        return Results.Ok(pending);
    });

app.Run();

namespace Sample.BatchWorkflow
{
    /// <summary>Marker for WebApplicationFactory test discovery.</summary>
    public partial class Program;
}
