# UKBatch.Transport.RabbitMQ

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

RabbitMQ (AMQP) transport adapter for the [UKBatch](https://github.com/nspukcode-hub/UKBatch) batch / job orchestration ecosystem. Implements `ITransport` over a broker so a batch can dispatch a step to a different microservice through durable, persistent messaging.

> **v0.1.0-alpha** — broker-backed cross-service transport. Same `JobMessage` / `JobResult` envelope as `UKBatch.Transport.Http`, different wire.

## What is UKBatch.Transport.RabbitMQ?

`UKBatch.Transport.RabbitMQ` lets two (or more) UKBatch-hosting microservices talk to each other through a RabbitMQ broker instead of point-to-point HTTP. The orchestrator publishes a `JobMessage`; the broker routes it to the target service's durable queue; the worker consumes, runs the job, and (for request/reply steps) returns a `JobResult`.

Position in the package family:

```
UKBatch.Abstractions       (zero-dep interfaces)
UKBatch.Core               (runtime, scheduler, in-memory stores, in-process transport)
UKBatch.AspNetCore         (IHostedService, DI integration)
UKBatch.Transport.Http     (cross-service ITransport over HTTP)
UKBatch.Transport.RabbitMQ (cross-service ITransport over AMQP)   ← this package
UKBatch.Storage.EntityFrameworkCore (PostgreSQL / SQLite)
```

## Install

```bash
dotnet add package UKBatch.Abstractions
dotnet add package UKBatch.Core
dotnet add package UKBatch.AspNetCore
dotnet add package UKBatch.Transport.RabbitMQ
```

## Quickstart

```csharp
using UKBatch.AspNetCore;
using UKBatch.Transport.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o => o.ThisServiceName = "billing-worker");   // = service queue name suffix
    b.AddJob<InvoiceProcessingJob>().Named("InvoiceProcessing");
});

// Replace the default InProcessTransport singleton with the RabbitMQ adapter.
builder.Services.AddUKBatchRabbitMqTransport(opts =>
{
    opts.Uri = builder.Configuration["UKBatch:Transport:RabbitMQ:Uri"];   // amqp://user:pass@host:5672/vhost
    // or discrete fields: opts.HostName / opts.UserName / opts.Password / opts.UseTls
});

var app = builder.Build();
app.Run();
```

The orchestrator side configures the same way but targets a worker via a cross-service step (`.OnService("billing-worker")`), exactly as with the HTTP transport — only the job NAME is shared.

## Topology

Declared idempotently on connect:

| Object | Default name | Type | Notes |
|---|---|---|---|
| Job exchange | `ukbatch.jobs` | direct, durable | routing key = target service name |
| Service queue | `ukbatch.service.{serviceName}` | durable **quorum** | `x-delivery-limit` (broker-enforced) → DLX on exhaustion |
| Dead-letter exchange | `ukbatch.jobs.dlx` | fanout, durable | |
| Dead-letter queue | `ukbatch.dlq` | durable | bound to the DLX |
| Reply | `amq.rabbitmq.reply-to` | built-in direct-reply-to | request/reply, no queue declared |

All names are overridable through `RabbitMqTransportOptions`.

## Delivery semantics

- **Durable + persistent:** messages survive a broker restart (`Persistent` + quorum queue).
- **Publisher confirms:** every publish awaits the broker ack — "sent but not stored" is impossible.
- **Manual ack, no requeue:** the worker acks after the job reaches a terminal state (`Completed` **or** `Failed`). A failed job flows back as a `JobResult` (driving the batch `OnFailure` branch); it does **not** go to the DLQ.
- **Effectively-once:** a per-service MessageId dedupe cache replays the cached result on a duplicate delivery instead of re-running the job (in-memory in v0.1; persistent dedupe is a v0.2 concern).
- **Dead-letter is narrow:** only poison messages (undeserializable / unregistered job) and broker delivery-limit exhaustion reach `ukbatch.dlq`.

## Security

There is **no application-level HMAC** — trust lives at the broker layer. Provision a dedicated user/password (not `guest`) and enable TLS (`opts.UseTls = true` or an `amqps://` URI) in production.

## Configuration options

`RabbitMqTransportOptions` binds from `UKBatch:Transport:RabbitMQ`. Defaults:

| Option | Default | Purpose |
|---|---|---|
| `Uri` | `null` | Full AMQP URI (takes precedence over discrete fields) |
| `HostName` / `Port` / `VirtualHost` / `UserName` / `Password` / `UseTls` | `localhost` / `5672` / `/` / `guest` / `guest` / `false` | Discrete connection fields |
| `ExchangeName` / `DeadLetterExchangeName` / `DeadLetterQueueName` / `QueuePrefix` | `ukbatch.jobs` / `ukbatch.jobs.dlx` / `ukbatch.dlq` / `ukbatch.service.` | Topology names |
| `PrefetchCount` | `16` | Max unacked deliveries in flight |
| `MaxRedeliveryCount` | `5` | Broker delivery-limit before dead-lettering |
| `DefaultRequestTimeout` | `30s` | Request/reply wall-clock timeout |
| `PublisherConfirmTimeout` | `10s` | Per-publish confirm wait |
| `MessageIdCacheCapacity` | `4096` | Receiver-side dedupe LRU size |
| `ConsumerDispatchConcurrency` | `1` | Parallel consumer dispatch per channel |

## Limitations & roadmap

- **v0.1-alpha:** in-memory MessageId dedupe (resets on process restart).
- **v0.2:** persistent dedupe, shared-secret/credential rotation, OpenTelemetry instrumentation, per-service distinct resilience pipelines, durable workflow resume.

## License & support

MIT licensed. Source code, samples, issue tracker:

- GitHub: <https://github.com/nspukcode-hub/UKBatch>

Contributions welcome.
