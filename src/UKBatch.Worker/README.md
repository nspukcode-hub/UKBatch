# UKBatch.Worker

Worker-mode helper for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a lightweight, pluggable batch/job orchestration library for .NET 8 and .NET 10. One call, `b.UseWorkerMode(...)`, turns a microservice into a **worker** in a server + workers deployment: it advertises itself to the UKBatch server over an HTTP heartbeat and runs the jobs the orchestrator routes to it over a cross-service transport.

> **Status:** part of the UKBatch 0.1.0-alpha package family.

## Install

```bash
dotnet add package UKBatch.Worker
```

## Quick start

Register your jobs, opt the host into worker mode, and add a cross-service transport. The worker exposes a health endpoint and stays alive to consume work:

```csharp
using UKBatch.AspNetCore;
using UKBatch.Transport.RabbitMQ;
using UKBatch.Worker;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.AddJob<GenerateInvoiceJob>().Named("GenerateInvoice");

    b.UseWorkerMode(w =>
    {
        w.WorkerName = "invoicing";                       // routing key — see below
        w.ServerUrl  = "http://ukbatch-server:8080";      // heartbeat target
    });
});

// A cross-service transport is REQUIRED in worker mode.
builder.Services.AddUKBatchRabbitMqTransport();

var app = builder.Build();
app.MapHealthChecks("/healthz");
app.Run();
```

The transport line can equally be `builder.Services.AddUKBatchHttpTransport(...)`; the call order between `UseWorkerMode` and the transport registration does not matter.

## **`WorkerName` is the routing key**

`WorkerName` is not just a label — it is how the orchestrator finds this worker. It MUST match (ordinal, case-sensitive) the name used by `.OnService("...")` in the server's batch definitions:

```csharp
// On the server, in a batch definition:
batch.RunJob("GenerateInvoice", step => step.OnService("invoicing"));
```

A mismatch is **silent**: the message is routed to a queue nobody consumes, the step never runs, and the worker stays invisible in the dashboard. Match the names exactly.

## **A cross-service transport is required**

Worker mode needs UKBatch.Transport.RabbitMQ or UKBatch.Transport.Http registered. If neither is present, the host **fails fast at startup** with a clear error rather than starting a worker that can never receive work. The heartbeat alone does not carry dispatch — it is observability only.

## WorkerOptions

| Property | Type | Default | Notes |
|---|---|---|---|
| `WorkerName` | `string` | *(required)* | Logical worker name; the routing key. Must be non-whitespace and match `.OnService("...")`. |
| `ServerUrl` | `string?` | `null` | Base URL the heartbeat POSTs to. Required when `Heartbeat` is true. |
| `Tags` | `string[]?` | `null` | Free-form tags shown in the dashboard Workers panel (observability). |
| `Heartbeat` | `bool` | `true` | When false, no heartbeat is sent — the worker is invisible in the dashboard but dispatch still works. |
| `HeartbeatInterval` | `TimeSpan` | `15s` | Heartbeat cadence. The server marks a worker offline when no heartbeat arrives for 45 seconds, so keep this comfortably below that. |
| `ApiKey` | `string?` | `null` | Reserved for a future release; currently unused. |

## When to use it

Use this package only on **worker services in a server + workers topology** — microservices that execute jobs an external UKBatch server dispatches to them. You do **not** need it in embedded mode, where the runtime and dashboard live in the same application; there, reference UKBatch.AspNetCore (or UKBatch.Core) directly.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [UKBatch on GitHub](https://github.com/nspukcode-hub/UKBatch).
