# UKBatch.Api

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

REST API + OpenAPI 3.1 + SignalR push hub for the [UKBatch](https://github.com/nspukcode-hub/UKBatch) batch / job orchestration ecosystem.

> **v0.1.0-alpha** — public API surface is approaching stability. Adapter packages (HTTP transport, EF Core / Redis storage, RabbitMQ) ship as separate NuGet packages.

## What is UKBatch.Api?

`UKBatch.Api` mounts a REST surface and a SignalR hub onto any `RouteGroupBuilder` in an ASP.NET Core app. It is auth-agnostic: anonymous by default, opt-in via `RequireAuthorization` on the group. The hub broadcasts execution / progress / approval / batch-completion events for dashboard clients.

Position in the package family:

```
UKBatch.Abstractions   (zero-dep interfaces)
UKBatch.Core           (runtime, scheduler, in-memory stores, in-process transport)
UKBatch.AspNetCore     (IHostedService, DI integration)
UKBatch.Api            (REST + OpenAPI + SignalR)   ← this package
UKBatch.Dashboard      (Blazor Server UI)
UKBatch.Transport.*    (HTTP / RabbitMQ / Kafka / AzureServiceBus)
UKBatch.Storage.*      (EF Core / Redis)
```

## Install

```bash
dotnet add package UKBatch.Abstractions
dotnet add package UKBatch.Core
dotnet add package UKBatch.AspNetCore
dotnet add package UKBatch.Api
```

## Quickstart

```csharp
using UKBatch.Abstractions.Jobs;
using UKBatch.Api;
using UKBatch.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.AddJob<MyJob>();
});

builder.Services.AddUKBatchApi();

var app = builder.Build();

app.MapGroup("/api").MapUKBatchApi();
app.MapOpenApi();   // /openapi/v1.json

app.Run();

public sealed class MyJob : IJob
{
    public Task ExecuteAsync(JobContext ctx, CancellationToken ct)
    {
        ctx.Logger.LogInformation("Hello from MyJob");
        return Task.CompletedTask;
    }
}
```

Trigger a job:

```bash
curl -X POST http://localhost:5000/api/jobs/MyJob/trigger \
  -H "Content-Type: application/json" \
  -d '{}'
```

A full demonstration lives under `samples/Sample.RestApi/`.

## REST surface

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/jobs` | List registered job definitions |
| `GET` | `/jobs/{name}` | Get job definition |
| `POST` | `/jobs/{name}/trigger` | Trigger a standalone job |
| `GET` | `/batches` | List batch definitions across sources |
| `GET` | `/batches/by-id/{id}` | Get batch definition by id |
| `GET` | `/batches/by-name/{name}` | Get batch definition by name |
| `POST` | `/batches/by-id/{id}/run` | Run a batch by definition id |
| `POST` | `/batches/by-name/{name}/run` | Run a batch by definition name |
| `POST` | `/batches` | Create a Dashboard/Api-source batch |
| `PUT` | `/batches/by-id/{id}` | Update a Store-source batch (optimistic concurrency) |
| `DELETE` | `/batches/by-id/{id}` | Delete a Store-source batch (idempotent) |
| `GET` | `/batches/{batchRunId}/status` | Get executions for a batch run |
| `GET` | `/executions/{id}` | Get a single execution |
| `POST` | `/executions/query` | Paginated query (POST so `JobQuery` array filters fit) |
| `POST` | `/executions/{id}/cancel` | Cancel an execution (idempotent on terminal) |
| `GET` | `/approvals` | List pending approval gates |
| `POST` | `/approvals/{id}/approve` | Approve a pending gate |
| `POST` | `/approvals/{id}/reject` | Reject a pending gate (reason required) |

## SignalR hub

Default path: `/hubs/jobs` (configurable via `UKBatchOptions.HubPath`).

Server-side subscribe methods:

| Method | Group |
|---|---|
| `SubscribeToExecution(executionId)` | `exec:{executionId}` |
| `SubscribeToBatch(batchRunId)` | `batch:{batchRunId}` |
| `SubscribeToJob(jobName)` | `job:{jobName}` |
| `SubscribeAll()` | `all` |

Client RPC methods (received from server):

| Method | When |
|---|---|
| `ExecutionStateChanged(JobExecution)` | Any execution state transition (Pending → Running → Completed/Failed/Cancelled) |
| `ProgressUpdated(ProgressBeat)` | Debounced progress for partitioned / long-running jobs |
| `ApprovalRequested(PendingApproval)` | New approval gate awaits resolution |
| `BatchCompleted(BatchCompletionSummary)` | Batch run finished (terminal aggregate) |

### Critical contracts

**Fan-out:** Clients subscribed to multiple matching groups (e.g. both `exec:{id}` and `all`) receive each event ONCE PER MATCHING GROUP — up to 4 times. Clients **MUST dedupe** by:
- `(ExecutionId, Status, AttemptNumber)` for `ExecutionStateChanged`
- `(BatchId, FinalStatus)` for `BatchCompleted`

**Reconnect:** After `WithAutomaticReconnect`'s `Reconnected` fires, group memberships are LOST. Clients **MUST re-subscribe** to any active groups.

```csharp
var connection = new HubConnectionBuilder()
    .WithUrl("http://localhost:5000/hubs/jobs")
    .WithAutomaticReconnect()
    .Build();

connection.On<JobExecution>("ExecutionStateChanged", e =>
{
    // dedupe by (ExecutionId, Status, AttemptNumber) client-side
});

connection.Reconnected += async _ =>
{
    // re-subscribe to active groups after reconnect
    await connection.InvokeAsync("SubscribeAll");
};

await connection.StartAsync();
await connection.InvokeAsync("SubscribeAll");
```

## Authorization

UKBatch.Api ships **auth-off by default**. Opt in at the route group level:

```csharp
app.MapGroup("/api")
   .RequireAuthorization("approval-policy")
   .MapUKBatchApi();
```

**Dual-mount pattern:** mount the SAME surface twice — anonymous + secured.

```csharp
app.MapGroup("/api").MapUKBatchApi();                              // anonymous
app.MapGroup("/api/secured")
    .MapUKBatchApi("Secured")                                      // op-id prefix
    .RequireAuthorization();                                       // protected
```

The `operationIdPrefix` parameter prevents OpenAPI operation-id collisions across the two mounts.

**Approver identity** is derived EXCLUSIVELY from `HttpContext.User`. The request body NEVER contributes identity or roles. `ApprovalRoleClaimTypes` configures which claim type(s) to scan (default `[ClaimTypes.Role]`; configure additional via `appsettings.json` for IdentityServer / Azure AD / SAML).

## ProblemDetails error map

| Type URI | Status | When |
|---|---|---|
| `ukbatch:batch-not-found` | 404 | Batch RUN id not in store |
| `ukbatch:batch-definition-not-found` | 404 | Batch definition id not in catalog |
| `ukbatch:batch-definition-duplicate-name` | 409 | Name collision on Create / Update |
| `ukbatch:job-not-registered` | 404 | Unknown job name on trigger |
| `ukbatch:execution-not-found` | 404 | Execution id missing on get / cancel |
| `ukbatch:approval-not-pending` | 404 | Approval id absent or resolved |
| `ukbatch:forbidden` | 403 | Approver lacks any required role |
| `ukbatch:approval-config-invalid` | 500 | Gate `AllowedRoles` empty (fail-safe deadlock) |
| `ukbatch:validation-failed` | 400 | Request validation failed (field errors in `errors`) |
| `ukbatch:concurrency-conflict` | 409 | Optimistic concurrency mismatch on Update |

## Configuration options

`UKBatchOptions` is configured via `IOptions<UKBatchOptions>`. Defaults:

| Option | Default | Purpose |
|---|---|---|
| `MaxDegreeOfParallelism` | `ProcessorCount` | Concurrent dispatcher in-flight executions |
| `DispatcherChannelCapacity` | `MaxDegreeOfParallelism * 32` | Trigger backpressure queue |
| `DefaultMaxRetries` | `0` | Default retry budget when not set per-job |
| `DefaultTimeoutSeconds` | `0` (no timeout) | Default per-execution timeout |
| `DefaultPartitionWorkerCount` | `ProcessorCount` | Default for `IPartitionedJob<T>` |
| `ShutdownTimeout` | `30s` | StopAsync drain time |
| `WatchBufferCapacity` | `1024` | Per-subscriber WatchAsync channel size |
| `ProgressFlushInterval` | `250ms` | Progress beat debounce |
| `CronFormat` | `IncludeSeconds` | 6-field default |
| `HubBufferCapacity` | `256` | SignalR fan-out per-pump buffer |
| `MaxPageLimit` | `500` | REST page size cap |
| `DefaultPageLimit` | `50` | REST default page size |
| `HubPath` | `/hubs/jobs` | SignalR hub URL |
| `MaxQueryStatusesCount` | `20` | Cap on `Statuses[]` filter array |
| `MaxQuerySearchTextLength` | `1024` | Cap on `SearchText` filter |
| `ApprovalRoleClaimTypes` | `[ClaimTypes.Role]` | Claim types scanned for approver roles |

## OpenAPI

`app.MapOpenApi()` from `Microsoft.AspNetCore.OpenApi` is auto-wired by `AddUKBatchApi()`. Customize via `IOpenApiOperationTransformer` / `IOpenApiSchemaTransformer`. Default transformers annotate every operation with 400/403/404/409/500 ProblemDetails responses and render enums as strings.

## Limitations & related packages

- By default `UKBatch.Api` uses in-memory stores (`InMemoryJobStore`, `InMemoryBatchDefinitionStore`) — all state is process-local. Swap in a persistent store to survive restarts.
- **HTTP Transport** for cross-service triggers — `UKBatch.Transport.Http`
- **SQL Storage** via EF Core (persistent batches/executions) — `UKBatch.Storage.EntityFrameworkCore`
- **RabbitMQ Transport** — `UKBatch.Transport.RabbitMQ`
- **Server + Workers deployment** — standalone server + Docker image (`ukbatch/server:0.1.0-alpha`)

## License & support

MIT licensed. Source code, samples, issue tracker:

- GitHub: <https://github.com/nspukcode-hub/UKBatch>

Contributions welcome.
