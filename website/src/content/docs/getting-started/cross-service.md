---
title: Cross-service workflows
description: Run a batch step on a different microservice over the HTTP or RabbitMQ transport.
---

A batch step can run on a different microservice. Prefix it with `.OnService("worker-name")`
and reference the job by **name** (so the orchestrator never needs the worker's job assembly):

```csharp
b.AddBatch("cross-service-demo", batch => batch
    .RunJob<PrepareOrderJob>()                                              // local
    .ThenRunJob("InvoiceProcessing", step => step.OnService("billing-worker"))  // remote
    .ThenRunJob<FinalizeOrderJob>());                                       // local again
```

The orchestrator and worker each register a cross-service transport. Two are available:

- **HTTP** (`UKBatch.Transport.Http`) — broker-free, point-to-point over HMAC-signed REST.
  Simplest to stand up; a dead receiver fails the step immediately. Good for low-latency
  request/reply between a few services. See
  [`samples/Sample.CrossServiceHttp`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.CrossServiceHttp).
- **RabbitMQ** (`UKBatch.Transport.RabbitMQ`) — broker-backed over durable quorum queues. A
  stopped worker's message **waits** in its queue until the worker restarts (durability), at
  the cost of running a broker. Good for resilient distributed dispatch. See
  [`samples/Sample.CrossServiceRabbitMQ`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.CrossServiceRabbitMQ).

Both use the same `JobMessage` / `JobResult` envelope, so the only difference is the wire and
the registration call.

:::note[Cross-service step output]
This preview does **not** forward a step's output as input to subsequent steps. Stateless
cross-service invocation works; a data pipeline that threads `step 1 output → step 2 input`
does not yet. See the [Changelog](/UKBatch/changelog/#known-limitations).
:::

Choosing a transport in depth: [UKBatch.Transport.Http](/UKBatch/packages/transport-http/)
· [UKBatch.Transport.RabbitMQ](/UKBatch/packages/transport-rabbitmq/).
