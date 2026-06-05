# Getting started with UKBatch

UKBatch is a lightweight, pluggable batch/job orchestration library for .NET 8 and .NET 10 microservices. It runs in two shapes from the same NuGet packages and the same job code:

- **Embedded mode** — reference the library inside one ASP.NET Core app. The runtime, REST API, and dashboard all live in that process. Best for small to mid-size services.
- **Server + workers** — run the standalone `UKBatch.Server` (a Docker app) as the orchestrator and dashboard; your microservices join as **workers** over a cross-service transport. Best for larger distributed systems.

You write a job once and decide later where state lives (in-memory or a database) and how services talk (in-process, HTTP, or RabbitMQ) — the job code does not change.

This guide walks the common path. Each section links to a runnable sample under [`samples/`](samples/) rather than repeating it.

## Prerequisites

- **.NET 10 SDK** to build this repo (the repo pins a version in `global.json`). Consuming apps need only their own SDK: the packages target both `net8.0` and `net10.0`, so a plain .NET 8 SDK project works.
- **Docker** (optional) — only for the EF Core PostgreSQL path and the server + workers Compose stack.

> Packages are not on NuGet yet (0.1.0-alpha). To follow along now, clone the repo and reference the projects, or build the packages with `dotnet pack`. The `dotnet add package` lines below are what consumers will use once published.

## 1. Embedded mode — your first job

A job is a class implementing `IJob`. Register it by name and trigger it over REST or from `IJobRunner`.

```csharp
using UKBatch.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.AddJob<HelloJob>().Named(nameof(HelloJob));
});

var app = builder.Build();

// Trigger from a request handler. IJobRunner is resolved from DI.
app.MapPost("/trigger/hello",
    async (IJobRunner runner, CancellationToken ct) =>
    {
        var execution = await runner.TriggerAsync(nameof(HelloJob), JobParameters.Empty, triggeredBy: null, ct);
        return Results.Ok(new { execution.ExecutionId });
    });

app.Run();

public sealed class HelloJob : IJob
{
    public Task ExecuteAsync(JobContext ctx, CancellationToken ct)
    {
        ctx.Logger.LogInformation("Hello from {Job}", nameof(HelloJob));
        return Task.CompletedTask;
    }
}
```

`AddUKBatchAspNetCore` applies the **in-memory store** and the **in-process transport** by default — nothing else is required to run. (Outside ASP.NET Core, register the runtime with `services.AddUKBatch(b => ...)`.)

Mounting the REST API (next section) gives you `POST /api/jobs/{name}/trigger` and a live SignalR hub instead of hand-rolling endpoints.

Runnable sample: [`samples/Sample.SimpleJob`](samples/Sample.SimpleJob) — a single job, a partitioned job, a scheduled job, and trigger endpoints.

## 2. Add the dashboard

The Blazor Server dashboard is a pure consumer of `UKBatch.Api` over HTTP/SignalR. In embedded mode you point it at your own loopback `/api`.

```csharp
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b => b.AddJob<HelloJob>());
builder.Services.AddUKBatchApi();

builder.Services.AddUKBatchDashboard(opts =>
{
    opts.Services.Add(new UKBatchServiceDescriptor
    {
        Name = "self",
        // Trailing slash is REQUIRED — see Gotchas.
        BaseUrl = new Uri("http://localhost:5050/api/"),
        DisplayName = "Local",
    });
});
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseAntiforgery();                  // REQUIRED for Razor Components — see Gotchas
app.MapGroup("/api").MapUKBatchApi();  // REST + SignalR hub at /api/hubs/jobs
app.MapUKBatchDashboard();             // UI at /dashboard
app.MapStaticAssets();                 // serves Blazor framework assets (.NET 9+; on .NET 8 use app.UseStaticFiles())

app.Run();
```

Open `http://localhost:5050/dashboard`. The Jobs, Batches, Executions, and Approvals pages appear immediately.

Runnable sample: [`samples/Sample.Dashboard`](samples/Sample.Dashboard).

## 3. Persistent storage (EF Core)

By default all state is in memory and resets on restart. Add `UKBatch.Storage.EntityFrameworkCore` to persist batch definitions, execution history, and approval records to **PostgreSQL** or **SQLite**.

```csharp
services.AddUKBatch(b => b.AddJob<HelloJob>())
        .AddUKBatchEntityFrameworkCoreStores(o => o.UseSqlite("Data Source=ukbatch.db"));
// or: o.UsePostgres("Host=localhost;Database=ukbatch;Username=ukbatch;Password=…")
```

Register the EF stores **after** `AddUKBatch` / `AddUKBatchApi` — they replace the in-memory registrations. The package ships design-time migrations for both providers; set `o.MigrateOnStartup = true` for dev, or run `dotnet ef database update` in production.

**Durability boundary:** a restart preserves history, definitions, and pending approval *records*, but it does **not** resume a paused workflow — in-flight executions are reaped to `Failed`. See [`src/UKBatch.Storage.EntityFrameworkCore/README.md`](src/UKBatch.Storage.EntityFrameworkCore/README.md) for the full contract.

`samples/Sample.RestApi` takes a `--storage inmemory|ef-sqlite|ef-pg` flag so you can watch the same app run on each store.

## 4. Cross-service workflows

A batch step can run on a different microservice. Prefix it with `.OnService("worker-name")` and reference the job by **name** (so the orchestrator never needs the worker's job assembly):

```csharp
b.AddBatch("cross-service-demo", batch => batch
    .RunJob<PrepareOrderJob>()                                   // local
    .ThenRunJob("InvoiceProcessing", step => step.OnService("billing-worker"))  // remote
    .ThenRunJob<FinalizeOrderJob>());                            // local again
```

The orchestrator and worker each register a cross-service transport. Two are available:

- **HTTP** (`UKBatch.Transport.Http`) — broker-free, point-to-point over HMAC-signed REST. Simplest to stand up; a dead receiver fails the step immediately. Good for low-latency request/reply between a few services. See [`samples/Sample.CrossServiceHttp`](samples/Sample.CrossServiceHttp).
- **RabbitMQ** (`UKBatch.Transport.RabbitMQ`) — broker-backed over durable quorum queues. A stopped worker's message **waits** in its queue until the worker restarts (durability), at the cost of running a broker. Good for resilient distributed dispatch. See [`samples/Sample.CrossServiceRabbitMQ`](samples/Sample.CrossServiceRabbitMQ).

Both use the same `JobMessage` / `JobResult` envelope, so the only difference is the wire and the registration call.

## 5. Server + workers with Docker Compose

Instead of embedding the runtime, run the generic `UKBatch.Server` as the orchestrator + dashboard and join microservices as workers. There is no published Docker image yet — build and run the full stack from the repo root:

```bash
docker compose up --build
```

This brings up PostgreSQL (host port `5433`), RabbitMQ (management UI `15672`), the server (dashboard at `http://localhost:5070/dashboard`), and three sample workers (`5170`/`5180`/`5190`). The server is configured entirely by environment variables:

| Variable | Values | Purpose |
|---|---|---|
| `UKBATCH_SERVICE_NAME` | string | The server's own service identity |
| `UKBATCH_STORAGE` | `inmemory` \| `ef-sqlite` \| `ef-pg` | State backend |
| `UKBATCH_STORAGE_CONNECTION` | connection string | Required for the EF backends |
| `UKBATCH_TRANSPORT` | `inprocess` \| `http` \| `rabbitmq` | Cross-service transport |
| `UKBATCH_ENABLE_DASHBOARD` | `true` \| `false` | Serve the dashboard |
| `UKBATCH_ALLOW_ANONYMOUS` | `true` \| `false` | Run anonymously — only behind a trusted network or an external auth gateway |
| `UKBATCH_DEV_AUTH` | `true` \| `false` (default false) | Development-only header auth — never use in production |

**The server is fail-closed on auth.** This release ships no production authentication scheme, so `UKBatch.Server` refuses to start unless you explicitly choose a posture: `UKBATCH_ALLOW_ANONYMOUS=true` (anonymous, for a trusted network or behind an auth gateway) or `UKBATCH_DEV_AUTH=true` (header-trusting, demos only). The Compose stack chooses `UKBATCH_DEV_AUTH=true`. Production-grade OIDC authentication is on the roadmap.

A worker opts in with `UseWorkerMode` plus a cross-service transport:

```csharp
builder.AddUKBatchAspNetCore(b =>
{
    b.AddJob<GenerateInvoiceJob>();          // [Job(Name = "GenerateInvoice")]
    b.UseWorkerMode(w =>
    {
        w.WorkerName = "invoicing";           // routing key — MUST match .OnService("invoicing")
        w.ServerUrl  = "http://ukbatch-server:8080";
    });
});
builder.Services.AddUKBatchRabbitMqTransport();   // a cross-service transport is REQUIRED
```

Two warnings worth repeating: `WorkerName` is the routing key and must match the step's `.OnService("...")` **exactly (ordinal)** — a mismatch is silent. And a worker that registers no cross-service transport **fails fast at startup** rather than starting unable to receive work. The server marks a worker offline after 45s without a heartbeat.

Runnable sample + an asserting end-to-end harness: [`samples/Sample.WorkerMode`](samples/Sample.WorkerMode).

## 6. Workflow building blocks

### Approval gates

Pause a batch until a human approves or rejects from the dashboard:

```csharp
b.AddBatch("rollout", batch => batch
    .RunJob<DeployJob>()
    .ThenWaitForApproval(
        title: "Confirm rollout",
        roles: new[] { "ops" },
        timeout: TimeSpan.FromMinutes(30),
        onTimeout: ApprovalTimeoutAction.Hold));
```

The gate holds until an authenticated caller with a matching role approves it. Roles are matched against `ClaimTypes.Role` by default — see Gotchas if you use Azure AD / Auth0 / SAML.

### Partitioned (data-parallel) jobs

For "fetch a set of items, then process them on N workers", implement `IPartitionedJob<TItem>`. The runtime owns the producer/consumer plumbing; you declare the source stream and the per-item work, with an optional commit hook:

```csharp
b.AddPartitionedJob<ReconcileInvoicesJob, InvoiceRow>()
    .Named("ReconcileInvoices")
    .WithParallelism(3)
    .WithItemErrorPolicy(ItemErrorPolicy.ContinueOnError);
```

`SourceAsync` streams items, `ProcessAsync` runs on N concurrent workers (must be thread-safe), and the optional `FinalizeAsync` commits once after every item. The worker count can be overridden per run with the trigger parameter `ukbatch.workers` (capped at 128).

### Attribute discovery

Instead of registering each job explicitly, decorate it with `[Job]` and scan assemblies:

```csharp
[Job(Name = "DailyReport", Schedule = "0 9 * * *", MaxRetries = 3, TimeoutSeconds = 600)]
public sealed class DailyReportJob : IJob { /* ... */ }

builder.AddUKBatchAspNetCore(b => b.ScanAssemblies(typeof(Program).Assembly));
```

`[Job]` carries optional `Name`, `Schedule` (cron), `MaxRetries`, `TimeoutSeconds`, and `Tags`.

## Gotchas

- **`app.UseAntiforgery()` is required when mapping the dashboard.** Razor Components emit anti-forgery metadata; without the middleware, `/dashboard` returns HTTP 500 ("endpoint contains anti-forgery metadata").
- **A dashboard service `BaseUrl` must end with a trailing slash** (`http://localhost:5050/api/`). `HttpClient.BaseAddress` drops the last path segment otherwise (RFC 3986), so `jobs` resolves to `/jobs` and 404s.
- **Referencing `UKBatch.Dashboard` via ProjectReference** (not NuGet) needs `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` in the host csproj — otherwise the dashboard renders as static HTML and buttons do nothing. NuGet consumers get this automatically via the package's build props.
- **Approval roles read `ClaimTypes.Role` by default.** Azure AD / Auth0 / SAML emit other claim types — configure `UKBatch:ApprovalRoleClaimTypes` in `appsettings.json` (it binds `UKBatchOptions.ApprovalRoleClaimTypes`), or approvals 403 even with the right role present.
- **macOS port 5000 is held by AirPlay Receiver** — it answers every request with `403`. The samples use ports 5050+; pick a non-5000 port or disable AirPlay Receiver.

## What's not here yet

This is a 0.1.0-alpha preview. Durable workflow *resume*, step output forwarding, cross-service progress forwarding, and worker→server authentication are not in this release, and Kafka / Azure Service Bus / Redis adapters are roadmap only. The full list is in the [CHANGELOG](CHANGELOG.md#known-limitations).

## Next steps

- Package docs: [`UKBatch.Core`](src/UKBatch.Core/README.md) · [`UKBatch.AspNetCore`](src/UKBatch.AspNetCore/README.md) · [`UKBatch.Api`](src/UKBatch.Api/README.md) · [`UKBatch.Dashboard`](src/UKBatch.Dashboard/README.md) · [`UKBatch.Worker`](src/UKBatch.Worker/README.md) · [`UKBatch.Transport.Http`](src/UKBatch.Transport.Http/README.md) · [`UKBatch.Transport.RabbitMQ`](src/UKBatch.Transport.RabbitMQ/README.md) · [`UKBatch.Storage.EntityFrameworkCore`](src/UKBatch.Storage.EntityFrameworkCore/README.md)
- All samples: [`samples/`](samples/)
