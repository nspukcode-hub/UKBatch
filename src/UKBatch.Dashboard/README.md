# UKBatch.Dashboard

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

Blazor Server centralized dashboard for the [UKBatch](https://github.com/nspukcode-hub/UKBatch) batch / job orchestration ecosystem — monitoring, triggering, approvals, and a visual batch editor. Multi-service via `IUKBatchServiceRegistry`; subscribes to REST + SignalR endpoints exposed by `UKBatch.Api`.

> **v0.1.0-alpha** — backend infrastructure + 10 pages + live row + connection banner, plus the Create/Edit wizard and the drag-drop visual batch editor.

## What is UKBatch.Dashboard?

`UKBatch.Dashboard` is a Blazor Server-hosted UI that surfaces UKBatch state across one or many services. It is purely a consumer of `UKBatch.Api` — every interaction goes through HTTP / SignalR, NEVER directly into the runtime. As a result the same package powers both deployment modes:

- **Embedded mode:** dashboard + REST + runtime live in the same process. Configure a single `self` service descriptor pointing at the loopback URL.
- **Server + workers mode (central dashboard):** one dashboard host fans out to N microservices, each exposing UKBatch.Api. Configure one `UKBatchServiceDescriptor` per service.

Position in the package family:

```
UKBatch.Abstractions   (zero-dep interfaces)
UKBatch.Core           (runtime, scheduler, in-memory stores, in-process transport)
UKBatch.AspNetCore     (IHostedService, DI integration)
UKBatch.Api            (REST + OpenAPI + SignalR)
UKBatch.Dashboard      (Blazor Server UI)   <- this package
UKBatch.Transport.*    (HTTP / RabbitMQ / Kafka / AzureServiceBus)
UKBatch.Storage.*      (EF Core / Redis)
```

## Install

```bash
dotnet add package UKBatch.Dashboard
```

The package transitively brings `UKBatch.Api`, `UKBatch.AspNetCore`, `UKBatch.Core`, and `UKBatch.Abstractions`. For embedded deployments that is the complete dependency set; for server + workers deployments you only need this package on the dashboard host (the worker services pull in `UKBatch.Api` independently).

## Host project setup

UKBatch.Dashboard ships its own `App.razor`, `Routes.razor`, and all Razor Pages, so consumer projects do **not** need to author them. However, the .NET Web SDK's framework-asset detection looks for `.razor` files in the **host** project to decide whether to emit `_framework/blazor.web.js` into the static-web-assets manifest (see `Microsoft.NET.Sdk.Web.ProjectSystem.targets`). When all `.razor` files live inside this NuGet package, the detection misses and the dashboard renders as static HTML — buttons silently do not respond.

**With PackageReference (recommended):** Nothing to do — the NuGet package ships `build/UKBatch.Dashboard.props`, which sets `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` automatically in your project at build time.

**With ProjectReference (solution-internal samples and integration tests):** NuGet `build/*.props` files are NOT propagated through ProjectReference. Add the property to your host csproj manually:

```xml
<PropertyGroup>
  <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
</PropertyGroup>
```

To verify, after `dotnet build` check the manifest:

```bash
grep -c blazor.web.js bin/Debug/net*/YourApp.staticwebassets.endpoints.json
```

A value of `0` is the bug. Expected value is `>= 1` (typically 12, counting fingerprinted + gzipped variants).

## Setup recipe (embedded mode)

```csharp
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.AddJob<MyJob>();
});
builder.Services.AddUKBatchApi();

// Required for Razor Components (the dashboard emits anti-forgery metadata).
builder.Services.AddAntiforgery();

builder.Services.AddUKBatchDashboard(opts =>
{
    opts.Services.Add(new UKBatchServiceDescriptor
    {
        Name = "self",
        // Trailing slash IS required — see "BaseUrl gotcha" below.
        BaseUrl = new Uri("http://localhost:5000/api/"),
        DisplayName = "Local",
    });
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

// REST surface + SignalR hub.
app.MapGroup("/api").MapUKBatchApi();

// Dashboard at literal /dashboard/...
app.MapUKBatchDashboard();

// MapStaticAssets is the .NET 9+ static asset manifest endpoint, required for
// `_framework/blazor.web.js` + fingerprinted assets. `UseStaticFiles` alone does NOT serve
// Razor Components framework files.
app.MapStaticAssets();

app.Run();
```

Open `http://localhost:5000/dashboard` and the Jobs / Batches / Executions / Approvals pages appear immediately.

### Critical gotchas

**`app.UseAntiforgery()` is REQUIRED.** Razor Components emit anti-forgery metadata; without `UseAntiforgery()` middleware in the pipeline, requests return HTTP 500 with the message *"Endpoint /dashboard contains anti-forgery metadata, but a middleware was not found that supports anti-forgery."* Place `UseAntiforgery()` after `UseAuthorization()`.

**`BaseUrl` MUST end with a trailing slash.** `HttpClient.BaseAddress` per RFC 3986 strips the last segment when joining a relative URI, so:

| BaseUrl | Relative call | Resolved URL |
|---|---|---|
| `http://localhost:5000/api/` (good) | `jobs` | `http://localhost:5000/api/jobs` |
| `http://localhost:5000/api` (bad)   | `jobs` | `http://localhost:5000/jobs` (404) |

The validator does not enforce this in v0.1; v0.2 may auto-append.

## Service registry config

The dashboard discovers services via `UKBatch:Dashboard:Services[]` in `appsettings.json` and merges any in-code entries from the `configure` callback:

```jsonc
{
  "UKBatch": {
    "Dashboard": {
      "Services": [
        {
          "Name": "self",
          "BaseUrl": "http://localhost:5000/api/",
          "DisplayName": "Local"
        },
        {
          "Name": "orders",
          "BaseUrl": "http://orders.internal:5000/api/",
          "DisplayName": "Orders Service",
          "Tags": [ "prod", "eu-west" ],
          "ApiKey": "...",
          "HubPath": "/hubs/jobs"
        }
      ]
    }
  }
}
```

`Name` MUST match the regex `^[a-z][a-z0-9-]*$` (kebab-case) — it is the URL path segment for `/dashboard/{name}/...`. Duplicates fail at host startup. `ApiKey` is reserved for v0.2 cross-service auth and not consumed at the REST / hub layer in v0.1.

## Deployment modes

### Embedded mode

Single process hosts both `UKBatch.Api` and `UKBatch.Dashboard`. Loopback URL in the service descriptor. Best for small / mid-size apps.

### Server + workers mode (central dashboard)

One dashboard host, N microservice hosts each running `UKBatch.Api`. Each microservice exposes the REST + hub; the dashboard adds one `UKBatchServiceDescriptor` per service. The sidebar groups by `Tags`. Architecturally identical to embedded mode — only the descriptor's `BaseUrl` differs.

## Authorization

UKBatch.Dashboard ships **auth-off by default**. Opt in at the `MapUKBatchDashboard()` return value:

```csharp
app.MapUKBatchDashboard()
   .RequireAuthorization();
```

The dashboard does NOT call `AddAuthentication` / `AddAuthorization` for you — you choose the scheme (Cookie / OIDC / JWT / etc.). The Sample project uses a header-based DevAuth scheme for integration tests; production deployments should swap in their identity provider of choice.

## Pages

| Route | Purpose |
|---|---|
| `/dashboard` | Landing — service tiles + connection health |
| `/dashboard/settings` | Theme + service registry inspection |
| `/dashboard/{service}/jobs` | Job catalog |
| `/dashboard/{service}/jobs/{name}` | Job detail + recent executions |
| `/dashboard/{service}/batches` | Batch definition catalog (Code + Dashboard + Api sources) |
| `/dashboard/{service}/batches/{id}` | Batch definition detail + recent runs |
| `/dashboard/{service}/runs/{batchRunId}` | Batch run live progress (step DAG + per-step executions) |
| `/dashboard/{service}/executions` | Cross-job execution query (filterable) |
| `/dashboard/{service}/executions/{id}` | Single execution live snapshot + cancel action |
| `/dashboard/{service}/approvals` | Pending approval queue (approve / reject with note / reason) |
| `/dashboard/{service}/dag` | Step DAG tree-view (preview) |
| `/dashboard/{service}/wizard` | Placeholder route — visual batch definition builder |

## SignalR contract

The dashboard wraps `UKBatch.Api`'s hub (`/api/hubs/jobs`) through `IUKBatchClient`. Per-service the client is a **singleton** — page components share the SAME `RestUKBatchClient` instance and subscribe / unsubscribe to events at the C# event-handler level, NOT at the SignalR group level. The hub group plumbing is internal to the client.

### Client RPC events

| Event | Payload | Dedupe key |
|---|---|---|
| `ExecutionStateChanged` | `JobExecution` | `(ExecutionId, Status, AttemptNumber)` |
| `ProgressUpdated` | `ProgressBeat` | `(ExecutionId, Processed, Failed)` (best-effort) |
| `ApprovalRequested` | `PendingApproval` | none (rare) |
| `BatchCompleted` | `BatchCompletionSummary` | `BatchId` |

The internal LRU dedupe cache filters duplicates that arise because a single execution event reaches the client up to 4 times (subscribed to `exec:{id}` + `batch:{id}` + `job:{name}` + `all` simultaneously). Cache capacity defaults to **256 entries per stream**, tunable via `DashboardOptions.DedupeCacheCapacity`.

### B1 monotonic Status rank guard

The subscribe-first-then-fetch pattern races: events queued before the fetch returns can arrive with an older `Status` than the snapshot. The row + execution-detail components MUST drop stale events to avoid backwards UI flicker (e.g. `Completed -> Pending -> Running -> Completed`). The guard:

| Rank | Statuses |
|---|---|
| 0 | `Pending`, `Scheduled` |
| 1 | `Running`, `AwaitingApproval`, `Retrying` |
| 2 | `Cancelling` |
| 3 | `Completed`, `Failed`, `Cancelled` |

Rules:
1. If the incoming event's rank is **lower** than the current state, drop it.
2. If ranks are equal and the incoming `AttemptNumber` is **lower**, drop it.
3. Otherwise, adopt the event.

Terminal states share rank 3 — once a row enters terminal, only a same-status higher-attempt event can replace it (which never happens in v0.1's runtime). The `LiveExecutionRow` + `Executions/Detail` page both enforce this client-side; tests in `LiveExecutionRowTests.StaleRunningAfterCompleted_DoesNotRegressMarkup` + `ExecutionsDetailTests.StaleRunningAfterCompleted_DoesNotRegressDetailMarkup` lock the contract.

### Reconnect contract

`HubConnection.WithAutomaticReconnect` fires `Reconnected` after a transient disconnect. SignalR **loses group memberships** on reconnect; the client re-subscribes to every tracked group automatically. Pages do NOT need to re-subscribe manually.

If ANY group re-subscribe fails, the client transitions to `UKBatchClientState.PartiallyConnected` — the `ConnectionBanner` surfaces an amber "Retry" affordance. Operator click drains failed groups via a fresh `Connect`. New subscribe calls STILL succeed in `PartiallyConnected` (NEW-SF-D v1.2): only PRE-EXISTING failed groups stay dark until manual recovery.

### `UKBatchClientState`

| State | Meaning |
|---|---|
| `Disconnected` | No active hub connection (initial state or after a clean disconnect) |
| `Connecting` | `HubConnection.StartAsync` is in flight |
| `Connected` | Hub healthy, all subscriptions live |
| `Reconnecting` | SignalR automatic-reconnect between `Reconnecting` and `Reconnected` |
| `PartiallyConnected` | Hub up but one or more pre-existing groups failed to re-subscribe — operator retry required |

## DashboardOptions reference

| Option | Default | Purpose |
|---|---|---|
| `Services` | `[]` | Registered UKBatch services (>= 1 required; order = sidebar render order) |
| `DefaultPageSize` | `50` | Default page size for paged lists (Jobs, Batches, Executions) |
| `ReconnectDelays` | `null` (jittered defaults) | Hub auto-reconnect delays — `[2s+rand(0,1s), 5s+rand(0,2s), 10s+rand(0,3s), 30s+rand(0,5s)]` |
| `DedupeCacheCapacity` | `256` | LRU dedupe cache size per event stream (exec / progress / batch-complete) |
| `HttpTimeout` | `30s` | HTTP request timeout for REST calls |

All options bind from `UKBatch:Dashboard:*` in `appsettings.json` and validate at host startup via `DashboardOptionsValidator`. Invalid config throws `OptionsValidationException` before the host enters the request pipeline.

## Production caveat — role claim configuration (SAML / Azure AD / Auth0)

The approval gate matches user roles against `ApprovalGateConfig.AllowedRoles`. By default `UKBatchOptions.ApprovalRoleClaimTypes` is `[ClaimTypes.Role]` (i.e. the standard .NET `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`). External identity providers commonly emit roles under different claim types:

- **Azure AD / Entra ID:** `roles` (or app-role claims under `wids`)
- **Auth0:** `https://your-tenant/roles` (configured custom claim)
- **SAML:** `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/role` is standard but tenants often customise

Configure additional claim types in `appsettings.json`:

```jsonc
{
  "UKBatch": {
    "ApprovalRoleClaimTypes": [
      "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
      "roles"
    ]
  }
}
```

Without this, approve / reject calls from SAML / Azure AD / Auth0 users return HTTP 403 ("approver lacks any required role") even when the role claim is present on the principal — the dashboard surfaces the ProblemDetails as a toast notification.

## Sample

A working end-to-end sample lives under `samples/Sample.Dashboard/`. It hosts an embedded deployment: Razor Components + `UKBatch.Api` + 5 jobs + an Approval-gated batch pipeline. The `DevAuth/` folder demonstrates a header-driven authentication scheme suitable for integration tests; production deployments should replace it with the actual identity provider.

## Limitations & roadmap

- **Mutating actions:** Cancel, Approve / Reject, and batch definition Create / Edit / Delete (via the Create/Edit wizard and the drag-drop visual editor, saved to `IBatchDefinitionStore`).
- **Storage:** all state flows through `UKBatch.Api`. The default stores are in-memory; `UKBatch.Storage.EntityFrameworkCore` provides persistent state.
- **Single-tenant:** v0.2.0+.

## License & support

MIT licensed. Source code, samples, issue tracker:

- GitHub: <https://github.com/nspukcode-hub/UKBatch>

Contributions welcome.
