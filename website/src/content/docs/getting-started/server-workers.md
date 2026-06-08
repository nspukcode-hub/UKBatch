---
title: Server + workers with Docker Compose
description: Run the standalone UKBatch.Server as orchestrator + dashboard, with microservices joining as workers.
---

Instead of embedding the runtime, run the generic `UKBatch.Server` as the orchestrator +
dashboard and join microservices as workers. There is no published Docker image yet — build
and run the full stack from the repo root:

```bash
docker compose up --build
```

This brings up PostgreSQL (host port `5433`), RabbitMQ (management UI `15672`), the server
(dashboard at `http://localhost:5070/dashboard`), and three sample workers
(`5170`/`5180`/`5190`). The server is configured entirely by environment variables:

| Variable | Values | Purpose |
|---|---|---|
| `UKBATCH_SERVICE_NAME` | string | The server's own service identity |
| `UKBATCH_STORAGE` | `inmemory` \| `ef-sqlite` \| `ef-pg` | State backend |
| `UKBATCH_STORAGE_CONNECTION` | connection string | Required for the EF backends |
| `UKBATCH_TRANSPORT` | `inprocess` \| `http` \| `rabbitmq` | Cross-service transport |
| `UKBATCH_ENABLE_DASHBOARD` | `true` \| `false` | Serve the dashboard |
| `UKBATCH_ALLOW_ANONYMOUS` | `true` \| `false` | Run anonymously — only behind a trusted network or an external auth gateway |
| `UKBATCH_DEV_AUTH` | `true` \| `false` (default false) | Development-only header auth — never use in production |

:::danger[The server is fail-closed on auth]
This release ships no production authentication scheme, so `UKBatch.Server` refuses to start
unless you explicitly choose a posture: `UKBATCH_ALLOW_ANONYMOUS=true` (anonymous, for a
trusted network or behind an auth gateway) or `UKBATCH_DEV_AUTH=true` (header-trusting, demos
only). The Compose stack chooses `UKBATCH_DEV_AUTH=true`. Production-grade OIDC authentication
is on the roadmap.
:::

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

Two warnings worth repeating:

- `WorkerName` is the routing key and must match the step's `.OnService("...")` **exactly
  (ordinal)** — a mismatch is silent.
- A worker that registers no cross-service transport **fails fast at startup** rather than
  starting unable to receive work.

The server marks a worker offline after 45s without a heartbeat.

:::tip[Runnable sample]
A full stack plus an asserting end-to-end harness:
[`samples/Sample.WorkerMode`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.WorkerMode).
:::
