# UKBatch

Lite, pluggable batch and job orchestration for .NET 8 and .NET 10 microservices.

> Status: 0.1.0-alpha — under active development; not yet on NuGet.

## What it is

UKBatch is a NuGet package family that orchestrates jobs and batches across one service or many, with minimal code. You write a job once and decide later where its state lives (in-memory or a database) and how services talk to each other (in-process, HTTP, or RabbitMQ) — the job code does not change. Run it embedded in your app with a built-in dashboard, or as a standalone server with worker microservices.

## Highlights

- **One-line integration** — `builder.AddUKBatchAspNetCore(b => b.AddJob<MyJob>())`; in-memory store and in-process transport are wired by default.
- **Two deployment modes, same code** — embed the library + dashboard in your app, or run a standalone server with workers. Only configuration differs.
- **Pluggable storage and transport** — storage: InMemory / PostgreSQL / SQLite (EF Core). Transport: in-process / HTTP / RabbitMQ.
- **Real workflow patterns** — sequential, parallel fan-out/fan-in, manual approval gates, and cross-service steps via `.OnService("...")`.
- **Data-parallel jobs** — "fetch a set, process on N workers" as a first-class primitive (`IPartitionedJob<TItem>`).
- **Dashboard + REST** — a Blazor Server UI for monitoring, triggering, approvals, and a visual batch editor, plus a REST API, OpenAPI document, and a live SignalR hub.

## Quick start — embedded

```csharp
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b => b.AddJob<SendWelcomeEmailsJob>());
builder.Services.AddUKBatchApi();
builder.Services.AddAntiforgery();
builder.Services.AddUKBatchDashboard(opts =>
    opts.Services.Add(new UKBatchServiceDescriptor
    {
        Name = "self",
        BaseUrl = new Uri("http://localhost:5050/api/"),   // trailing slash required
        DisplayName = "Local",
    }));

var app = builder.Build();
app.UseAntiforgery();                   // required for the dashboard
app.MapGroup("/api").MapUKBatchApi();
app.MapUKBatchDashboard();
app.MapStaticAssets();                  // .NET 9+; on .NET 8 call app.UseStaticFiles() instead
app.Run();
```

Open `/dashboard`.

```csharp
[Job(Name = "send-welcome-emails", Schedule = "0 9 * * *", MaxRetries = 3)]
public sealed class SendWelcomeEmailsJob : IJob
{
    public Task ExecuteAsync(JobContext ctx, CancellationToken ct) => /* your work */ Task.CompletedTask;
}
```

## Quick start — server + workers

There is no published Docker image yet — build and run the full stack from the repo root:

```bash
docker compose up --build
```

This brings up the orchestrator + dashboard (`http://localhost:5070/dashboard`), PostgreSQL (host port `5433`), RabbitMQ, and three sample workers. Each microservice joins as a worker:

```csharp
builder.AddUKBatchAspNetCore(b =>
{
    b.AddJob<GenerateInvoiceJob>();                 // [Job(Name = "GenerateInvoice")]
    b.UseWorkerMode(w =>
    {
        w.WorkerName = "invoicing";                  // routing key — matches .OnService("invoicing")
        w.ServerUrl  = "http://ukbatch-server:8080";
    });
});
builder.Services.AddUKBatchRabbitMqTransport();      // a cross-service transport is required
```

## Packages

### Available now

Every package targets `net8.0` and `net10.0` in a single NuGet package — your app's target framework picks the right build automatically. One .NET 8 caveat: the built-in OpenAPI document (`/openapi/v1.json`) requires .NET 9+, so `UKBatch.Api` on .NET 8 ships the full REST + SignalR surface without the generated document.

| Package | Purpose |
|---|---|
| `UKBatch.Abstractions` | Zero-dependency contracts (interfaces, attributes, DTOs) |
| `UKBatch.Core` | Runtime: dispatcher, scheduler, in-memory store, in-process transport |
| `UKBatch.AspNetCore` | ASP.NET Core host integration + readiness health check |
| `UKBatch.Worker` | `UseWorkerMode` helper for a server + workers deployment |
| `UKBatch.Api` | REST endpoints + OpenAPI document + SignalR hub |
| `UKBatch.Dashboard` | Blazor Server dashboard |
| `UKBatch.Transport.Http` | Broker-free cross-service transport (HMAC-signed REST) |
| `UKBatch.Transport.RabbitMQ` | RabbitMQ transport adapter (durable quorum queues) |
| `UKBatch.Storage.EntityFrameworkCore` | PostgreSQL / SQLite persistence (EF Core) |

`UKBatch.Server` is the standalone, configuration-driven orchestrator + dashboard built as a Docker app (`docker-compose.yml` at the repo root).

### Roadmap

Tracked toward v0.2: Kafka, Azure Service Bus, and Redis adapters; durable workflow resume; step output forwarding; cross-service progress forwarding; and worker→server authentication.

## Samples

| Sample | What it shows |
|---|---|
| [Sample.SimpleJob](samples/Sample.SimpleJob) | A single job, a scheduled job, a partitioned job, trigger endpoints |
| [Sample.BatchWorkflow](samples/Sample.BatchWorkflow) | Sequential, parallel, approval, and compensation batches |
| [Sample.Dashboard](samples/Sample.Dashboard) | Embedded dashboard over an approval-gated pipeline |
| [Sample.CrossServiceHttp](samples/Sample.CrossServiceHttp) | Cross-service steps over the HTTP transport |
| [Sample.CrossServiceRabbitMQ](samples/Sample.CrossServiceRabbitMQ) | Cross-service steps over RabbitMQ |
| [Sample.WorkerMode](samples/Sample.WorkerMode) | Full server + workers stack over Docker Compose |

## Documentation

- [GETTING_STARTED.md](GETTING_STARTED.md) — the guided walkthrough
- [CHANGELOG.md](CHANGELOG.md) — release notes and known limitations

## License

[MIT](LICENSE)
