---
title: Deployment modes
description: Embedded mode versus server + workers — the same code, only configuration differs.
---

UKBatch runs in two shapes from the same NuGet packages and the same job code. The
difference is configuration, not code.

## Embedded mode

Reference the library inside one ASP.NET Core app. The runtime, REST API, and dashboard all
live in that process. Best for small to mid-size services.

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

```csharp
[Job(Name = "send-welcome-emails", Schedule = "0 9 * * *", MaxRetries = 3)]
public sealed class SendWelcomeEmailsJob : IJob
{
    public Task ExecuteAsync(JobContext ctx, CancellationToken ct) => /* your work */ Task.CompletedTask;
}
```

Open `/dashboard`.

## Server + workers

Run the standalone `UKBatch.Server` (a Docker app) as the orchestrator and dashboard; your
microservices join as **workers** over a cross-service transport. Best for larger distributed
systems.

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

Architecturally a central dashboard is identical to embedded mode — it is just another
`UKBatchServiceDescriptor` pointing at a remote `/api`. See
[Server + workers with Docker Compose](/UKBatch/getting-started/server-workers/) for the full
stack and the environment-variable configuration.

## Which one?

| | Embedded | Server + workers |
|---|---|---|
| Topology | One app | Orchestrator + N microservices |
| Transport | In-process (default) | HTTP or RabbitMQ (required) |
| Dashboard | In the same app | Central, over remote `/api`s |
| Best for | Small / mid-size services | Larger distributed systems |

The job code is the same in both. You only change where state lives (in-memory or a database)
and how services talk (in-process, HTTP, or RabbitMQ).
