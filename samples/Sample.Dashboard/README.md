# Sample.Dashboard

Embedded-mode demo. Single host serves:

- The **REST API** surface (`UKBatch.Api`) at `/api/*`
- The **SignalR hub** at `/api/hubs/jobs`
- The **Blazor Server dashboard** (`UKBatch.Dashboard`) at `/dashboard/*`

The dashboard treats the local API as an HTTP target — the dashboard NEVER calls the in-process
runtime directly. This mirrors the server + workers deployment (a remote `ukbatch/server` container) byte-for-byte; only
the configured `BaseUrl` differs.

## Run

```bash
cd samples/Sample.Dashboard
dotnet run
```

Default port: `http://localhost:5000`. The `UKBatch:Dashboard:Services[]` entry in `appsettings.json`
points the dashboard at `http://localhost:5000/api/` (loopback).

> **BaseUrl trailing slash gotcha** — `HttpClient.BaseAddress` strips the last path segment on
> relative resolution per RFC 3986. So `BaseUrl = "http://x/api"` makes `client.GetAsync("jobs")`
> resolve to `http://x/jobs` (404). Always append a trailing `/`. The `DashboardOptionsValidator`
> does NOT enforce this in v0.1 — caller responsibility.

> If you also want to run **Sample.RestApi** concurrently, change one of the ports:
> - Sample.Dashboard: edit `Properties/launchSettings.json` → `applicationUrl`
> - Sample.RestApi: pass `--urls "http://localhost:5050"` and update the dashboard's
>   `UKBatch:Dashboard:Services[0].BaseUrl` accordingly.

## Smoke test

1. **Landing page** — browse `http://localhost:5000/dashboard`. You should see one service card
   labelled **Local** with a green health dot and counts (Jobs / Batches / Pending).
2. **Jobs catalog** — click the service card. You land on `/dashboard/self/jobs`. The catalog
   lists the 5 registered jobs (`InvoiceGenerationJob`, `EmailNotificationJob`, `ArchiveJob`,
   `RollbackJob`, `BulkArchiveJob`).
3. **Trigger a batch** — via curl:
   ```bash
   curl -X POST http://localhost:5000/api/batches/by-name/invoice-pipeline/run \
     -H "Content-Type: application/json" \
     -H "X-Dev-User: alice" -H "X-Dev-Roles: ops" \
     -d '{}'
   ```
   Note the `batchId` in the JSON response. Then navigate the dashboard to
   `/dashboard/self/runs/{batchId}` for the live progress view.
4. **Approve the gate** — the `invoice-pipeline` waits 5 s for `ops` approval before auto-approving.
   While it is `Awaiting`, browse `/dashboard/self/approvals` and click **Approve**. The row
   disappears (REST 204) and the batch advances.
5. **Reconnect banner** — kill the `dotnet run` process while a page is open, restart, and watch
   the `ConnectionBanner` cycle through `Reconnecting…` → green. (The dashboard reconnects via
   `HubConnection.WithAutomaticReconnect` and re-subscribes to active groups; integration tests
   lock the behaviour.)

## Pages overview

| Route | Description |
|---|---|
| `/dashboard` | Landing — multi-service health + counts |
| `/dashboard/{svc}/jobs` | Jobs catalog |
| `/dashboard/{svc}/jobs/{name}` | Job detail (paged executions) |
| `/dashboard/{svc}/batches` | Batches catalog |
| `/dashboard/{svc}/batches/by-id/{id}` | Batch definition detail (DAG tree) |
| `/dashboard/{svc}/runs/{batchRunId}` | Batch run live progress |
| `/dashboard/{svc}/executions` | Executions query / search |
| `/dashboard/{svc}/executions/{id}` | Execution detail (live progress) |
| `/dashboard/{svc}/approvals` | Pending approvals queue |
| `/dashboard/settings` | Theme + telemetry settings |

## Auth posture (production lock-down)

The sample mounts the dashboard with **no auth** (the v0.1 default). To require auth in
production, change `app.MapUKBatchDashboard()` to:

```csharp
app.MapUKBatchDashboard().RequireAuthorization();
```

(`SF3` invariant — `MapUKBatchDashboard` returns the `RazorComponentsEndpointConventionBuilder`
so callers can chain attribute conventions.)
