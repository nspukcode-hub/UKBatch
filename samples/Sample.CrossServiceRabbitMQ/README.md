# Sample.CrossServiceRabbitMQ — 2-process demo over a broker

End-to-end demo of `UKBatch.Transport.RabbitMQ` — a 3-step batch where step 2 crosses to another OS
process **via a RabbitMQ broker** (publish to a durable quorum service queue + reply over
direct-reply-to). Step 1 + step 3 run locally on the **orchestrator**; step 2 (`InvoiceProcessing`)
runs on the **worker**.

This is the broker-backed sibling of `Sample.CrossServiceHttp` — same 3-step batch shape, same
`OnService("billing-worker")` cross-service step, but the transport is AMQP instead of HMAC-signed HTTP.

## Prerequisites

* .NET 10 SDK
* Docker (for the RabbitMQ broker) — or a RabbitMQ instance already listening on `localhost:5672`

Ports used: **5060** (orchestrator) · **5160** (worker) · **5672** (AMQP) · **15672** (RabbitMQ
management UI). 5060/5160 avoid the macOS 12+ AirPlay/Control Center hold on port 5000.

## Three-terminal recipe

**Terminal 1** — start the broker:

```bash
cd samples/Sample.CrossServiceRabbitMQ
docker compose up rabbitmq
```

Wait for `Server startup complete`. The management UI is at <http://localhost:15672>
(user `guest` / pass `guest`).

**Terminal 2** — worker (declares + consumes `ukbatch.service.billing-worker`):

```bash
cd samples/Sample.CrossServiceRabbitMQ.Worker
dotnet run --urls http://localhost:5160
```

Wait for the consumer-pump start logs (connection + topology declared).

**Terminal 3** — orchestrator:

```bash
cd samples/Sample.CrossServiceRabbitMQ.Orchestrator
dotnet run --urls http://localhost:5060
```

Then trigger the batch:

```bash
curl -X POST http://localhost:5060/api/batches/by-name/cross-service-demo/run \
     -H "Content-Type: application/json" \
     -d '{}'
```

Open <http://localhost:5060/dashboard> to watch the batch run live.

## Verifying the cross-service hop

Watch the two app terminals while the batch runs:

* **Step 1** `PrepareOrderJob` — orchestrator terminal logs `generated orderId=… and forwarded it`
* **Step 2** `InvoiceProcessing` — **worker** terminal logs `processing orderId=… from source=orchestrator over RabbitMQ`
* **Step 3** `FinalizeOrderJob` — orchestrator terminal logs `cross-service step completed over RabbitMQ and returned invoiceId=…; finalizing`

The dashboard's batch detail page annotates step 2 with `TargetService=billing-worker`.

> The three steps demonstrate **step-output forwarding across the broker**: step 1 records the `orderId`
> via `context.Outputs.Set(...)`, the worker receives it as a parameter (`GetRequired<int>("orderId")`),
> produces an `invoiceId` output that rides the direct-reply-to reply back, and step 3 reads that
> `invoiceId` from its parameters — a full local → cross-service → local data round-trip.

## Inspecting the broker (management UI)

Open <http://localhost:15672> (`guest`/`guest`) and look under **Queues**:

* `ukbatch.service.billing-worker` — the worker's durable **quorum** service queue
* `ukbatch.service.orchestrator` — the orchestrator's own service queue (declared because it sets
  `ThisServiceName=orchestrator`; unused for sending)
* `ukbatch.dlq` — the dead-letter queue (messages land here after exceeding the delivery limit)

Under **Exchanges** you'll see `ukbatch.jobs` (direct) and `ukbatch.jobs.dlx` (fanout DLX).

## Kill-worker durability test (the broker's headline feature)

Unlike HTTP (where a dead receiver = immediate connection failure), RabbitMQ **persists** the message
in the durable quorum queue until a consumer acks it:

1. Stop the worker (Ctrl+C in Terminal 2). Leave the broker + orchestrator running.
2. Trigger a batch from the orchestrator (the `curl` above). Step 2's message is published to
   `ukbatch.service.billing-worker` and **waits** there — confirm via the management UI: the queue's
   **Ready** count is `1`. The orchestrator's `RequestReplyAsync` blocks up to `DefaultRequestTimeout`
   (30s) waiting for the reply.
3. Restart the worker (`dotnet run --urls http://localhost:5160`) **within the timeout window**. It
   reconnects, consumes the queued message, runs `InvoiceProcessing` and replies → the orchestrator's
   step 2 completes and step 3 runs.

   (If you exceed the 30s timeout, step 2 fails with a timeout and the message is still consumed on
   worker restart — but the batch run has already been marked failed. Restart within 30s to see the
   happy path.)

## Connection-resilience note

The broker connection uses RabbitMQ's automatic recovery. If the **broker** restarts while the apps
are running, the client transparently reconnects and re-declares topology — no app restart needed.
The Polly pipeline guards only the *initial* connect (retry `[2s, 5s, 15s]` + jitter, circuit breaker
5/30s); after the first successful connect, broker auto-recovery takes over.

## Docker compose alternative (full stack)

The `docker-compose.yml` next to this README also defines `orchestrator` + `worker` services that
build from per-service Dockerfiles. This sample does not ship those Dockerfiles, so **only the
`rabbitmq` service is runnable** (`docker compose up rabbitmq`) and the two apps run via `dotnet run`
as above. Running `docker compose up` (all services) fails with "Dockerfile not found" for the two
app services. (For a fully containerized server + workers stack, see `Sample.WorkerMode`.)

## Production checklist

* **Do not use `guest`/`guest`.** The default broker credentials only work over `localhost`. Provision
  a dedicated user/password and set them via env vars
  (`UKBatch__Transport__RabbitMQ__UserName`, `…__Password`) or a full
  `UKBatch__Transport__RabbitMQ__Uri=amqps://user:pass@host:5671/vhost`.
* **Enable TLS** (`UseTls=true` or an `amqps://` URI). There is no application-level HMAC in this
  transport — authentication and confidentiality live entirely at the broker layer.
* Size `PrefetchCount` to roughly the worker's max concurrent job slots, and scale throughput by
  running multiple worker instances (each consuming the same service queue) rather than raising
  per-channel concurrency (`ConsumerDispatchConcurrency` MUST stay `1` in v0.1).
