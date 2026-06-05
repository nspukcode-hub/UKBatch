# UKBatch.Api

REST endpoints, an OpenAPI document, and a SignalR push hub for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a lightweight, pluggable batch/job orchestration library for .NET 10. It mounts onto any `RouteGroupBuilder` in an ASP.NET Core app and is auth-agnostic: anonymous by default, opt-in via `RequireAuthorization`. The hub broadcasts execution, progress, approval, and batch-completion events for dashboard clients.

> **Status:** part of the UKBatch 0.1.0-alpha package family.

## Install

```bash
dotnet add package UKBatch.Api
```

`UKBatch.Api` brings `UKBatch.AspNetCore`, `UKBatch.Core`, and `UKBatch.Abstractions` transitively.

## Quick start

```csharp
using UKBatch.Api;
using UKBatch.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b => b.AddJob<MyJob>());
builder.Services.AddUKBatchApi();

var app = builder.Build();

app.MapGroup("/api").MapUKBatchApi();   // REST + SignalR hub
app.MapOpenApi();                       // /openapi/v1.json

app.Run();
```

Trigger a job: `curl -X POST http://localhost:5050/api/jobs/MyJob/trigger -H "Content-Type: application/json" -d '{}'`.

A full demonstration lives under [`samples/Sample.RestApi`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.RestApi).

## REST surface

The main route groups (mounted under whatever prefix you map):

| Group | Examples |
|---|---|
| Jobs | `GET /jobs`, `GET /jobs/{name}`, `POST /jobs/{name}/trigger` |
| Batches (definitions) | `GET /batches`, `GET /batches/by-name/{name}`, `POST /batches`, `PUT`/`DELETE /batches/by-id/{id}` |
| Batch runs | `POST /batches/by-name/{name}/run`, `GET /batches/{batchRunId}/status` |
| Executions | `GET /executions/{id}`, `POST /executions/query`, `POST /executions/{id}/cancel` |
| Approvals | `GET /approvals`, `POST /approvals/{id}/approve`, `POST /approvals/{id}/reject` |
| Workers | `GET /workers` (server + workers deployment registry) |

The complete surface — every route, request/response schema, and the ProblemDetails error map — is in the generated OpenAPI document at `/openapi/v1.json` (wired automatically by `AddUKBatchApi()`; default transformers annotate operations with 400/403/404/409/500 responses and render enums as strings).

## SignalR hub

The hub lives at `/hubs/jobs` (relative to the map prefix; configurable via `UKBatchOptions.HubPath`). Clients subscribe with `SubscribeToExecution` / `SubscribeToBatch` / `SubscribeToJob` / `SubscribeAll` and receive `ExecutionStateChanged`, `ProgressUpdated`, `ApprovalRequested`, and `BatchCompleted`.

Two client contracts matter:

- **Dedupe events.** A client subscribed to several matching groups receives each event once per group (up to 4 copies). Dedupe by `(ExecutionId, Status, AttemptNumber)` for executions and by `(BatchId, FinalStatus)` for batch completion.
- **Re-subscribe after reconnect.** SignalR loses group memberships on reconnect; re-subscribe to active groups when `Reconnected` fires.

## Authorization

Auth-off by default. Opt in at the route group, or mount the same surface twice — anonymous and secured:

```csharp
app.MapGroup("/api").MapUKBatchApi();                    // anonymous
app.MapGroup("/api/secured")
   .MapUKBatchApi("Secured")                             // operationId prefix avoids OpenAPI collisions
   .RequireAuthorization();
```

Approver identity is derived **exclusively** from `HttpContext.User` — the request body never contributes identity or roles. `UKBatchOptions.ApprovalRoleClaimTypes` selects which claim type(s) to scan (default `[ClaimTypes.Role]`; configure extras via `appsettings.json` for Azure AD / Auth0 / SAML).

## Critical notes

- The default stores are **in-memory** — all state is process-local and resets on restart. Add `UKBatch.Storage.EntityFrameworkCore` for persistence (register it after `AddUKBatchApi`).
- Errors are returned as RFC 9457 ProblemDetails with stable `ukbatch:*` type URIs (e.g. `ukbatch:job-not-registered`, `ukbatch:execution-not-found`).
- Useful `UKBatchOptions` — runtime: `MaxDegreeOfParallelism`, `DispatcherChannelCapacity`, `DefaultMaxRetries`; API surface: `HubPath`, `HubBufferCapacity`, `DefaultPageLimit` / `MaxPageLimit`, `MaxQueryStatusesCount`, `ApprovalRoleClaimTypes`.

## When to use it

Add this package when you want a REST surface and live status push over your UKBatch runtime — for an API client, the bundled dashboard, or a server + workers deployment. If you only need to run jobs in-process with no HTTP surface, `UKBatch.AspNetCore` (or `UKBatch.Core`) is enough.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [UKBatch on GitHub](https://github.com/nspukcode-hub/UKBatch).
