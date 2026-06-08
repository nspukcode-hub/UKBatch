---
title: Introduction
description: Install UKBatch, write your first job, and trigger it over REST — the guided walkthrough.
---

UKBatch is a lightweight, pluggable batch/job orchestration library for .NET 8 and
.NET 10 microservices. It runs in two shapes from the same NuGet packages and the
same job code:

- **Embedded mode** — reference the library inside one ASP.NET Core app. The runtime,
  REST API, and dashboard all live in that process. Best for small to mid-size services.
- **Server + workers** — run the standalone `UKBatch.Server` (a Docker app) as the
  orchestrator and dashboard; your microservices join as **workers** over a cross-service
  transport. Best for larger distributed systems.

You write a job once and decide later where state lives (in-memory or a database) and
how services talk (in-process, HTTP, or RabbitMQ) — the job code does not change.

This guide walks the common path. Each section links to a runnable sample on GitHub
rather than repeating it.

## Prerequisites

- **.NET 8 or .NET 10 SDK.** The packages target both `net8.0` and `net10.0`, so a plain
  .NET 8 SDK project works; the build for your target framework is selected automatically.
- **Docker** (optional) — only for the EF Core PostgreSQL path and the server + workers
  Compose stack.

:::note
Packages are on NuGet as 0.1.x-alpha pre-releases — install with
`dotnet add package <PackageId> --prerelease` (or tick "Include prerelease" in your IDE's
package manager).
:::

## Your first job

A job is a class implementing `IJob`. Register it by name and trigger it over REST or
from `IJobRunner`.

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

`AddUKBatchAspNetCore` applies the **in-memory store** and the **in-process transport**
by default — nothing else is required to run. (Outside ASP.NET Core, register the runtime
with `services.AddUKBatch(b => ...)`.)

Mounting the REST API ([next: add the dashboard](/UKBatch/getting-started/dashboard/))
gives you `POST /api/jobs/{name}/trigger` and a live SignalR hub instead of hand-rolling
endpoints.

:::tip[Runnable sample]
[`samples/Sample.SimpleJob`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.SimpleJob)
— a single job, a partitioned job, a scheduled job, and trigger endpoints.
:::

## Next steps

- [Add the dashboard](/UKBatch/getting-started/dashboard/)
- [Persistent storage with EF Core](/UKBatch/getting-started/storage/)
- [Cross-service workflows](/UKBatch/getting-started/cross-service/)
- [Server + workers with Docker Compose](/UKBatch/getting-started/server-workers/)
- [Workflow building blocks](/UKBatch/concepts/workflow-building-blocks/) — approval gates,
  partitioned jobs, attribute discovery
