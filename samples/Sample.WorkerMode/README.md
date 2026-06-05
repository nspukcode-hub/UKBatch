# Sample.WorkerMode — full server + workers demo (server + 2 workers over Docker Compose)

End-to-end server + workers demo: one `ukbatch/server` container (orchestrator + dashboard + worker registry)
plus three worker microservices (`invoicing`, `shipping`, `notification`), wired together by
**RabbitMQ** (cross-service transport) and **PostgreSQL** (durable server state). Two batches ship: a
simple two-step sequential run (invoice → ship), and an **approval + parallel** run (approval gate →
parallel{invoice, ship} → notify) that exercises all three workers — each step over the broker.

This is the packaged sibling of `Sample.CrossServiceRabbitMQ` (which runs orchestrator + worker via
`dotnet run`): here every process is a container and the orchestrator is the generic `UKBatch.Server`
image, not a hand-written app. The workers advertise themselves to the server via an HTTP heartbeat
(`UseWorkerMode`), so the dashboard **Workers** panel shows them Online/Offline live.

## Topology

| Service | Image | Host port | Role |
|---|---|---|---|
| `postgres` | `postgres:16-alpine` | 5432 | durable server state (EF Core storage) |
| `rabbitmq` | `rabbitmq:3.13-management` | 5672 / 15672 | cross-service transport + mgmt UI |
| `ukbatch-server` | `ukbatch/server:0.1.0-alpha` | **5070** | orchestrator + dashboard + `/api/workers/*` registry |
| `worker-invoicing` | sample image | 5170 | runs `GenerateInvoice` (`WorkerName=invoicing`) |
| `worker-shipping` | sample image | 5180 | runs `ShipOrder` (`WorkerName=shipping`) |
| `worker-notification` | sample image | 5190 | runs `SendNotification` (`WorkerName=notification`) |

Host ports avoid the macOS 12+ AirPlay/Control Center hold on port 5000. Inside the compose network
every app container listens on `:8080`.

## Prerequisites

* Docker + Docker Compose
* (optional) `curl` + `jq` to seed/trigger from the host

## 1. Bring the stack up

From the **repo root** (the `docker-compose.yml` lives there — every app image builds with the repo
root as its context):

```bash
docker compose up --build
```

First build pulls the .NET SDK + runtime base images and compiles the server + both workers, so the
initial run takes a few minutes. The server waits for Postgres + RabbitMQ to report healthy, then runs
EF migrations on startup (`MigrateOnStartup`). Watch for:

* `ukbatch-server` — `Now listening on: http://0.0.0.0:8080` + EF migration logs.
* `worker-invoicing` / `worker-shipping` — RabbitMQ consumer-pump start logs (connection + topology
  declared for `ukbatch.service.invoicing` / `ukbatch.service.shipping`).

## 2. Watch the Workers panel

Open the dashboard's Workers panel:

<http://localhost:5070/dashboard/self/workers>

Within ~15s (the heartbeat cadence) all three workers appear with a green **Online** badge, their job
names (`GenerateInvoice` / `ShipOrder` / `SendNotification`) and tags (`billing` / `fulfilment` /
`notify`). The panel is **observability only** — it reflects heartbeats; it is NEVER consulted for
dispatch (a worker missing from the panel still receives work over the broker).

## 3. Seed + trigger the cross-service batches

The server ships with **no** batch definitions of its own (it is a generic orchestrator). The helper
seeds **two** demos back-to-back — the simple two-step run below, then the approval + parallel run in
section 3a:

```bash
samples/Sample.WorkerMode/seed-batch.sh
```

For the **simple** demo it does two REST calls against `http://localhost:5070/api`:

```bash
# 1) Create an Api-source batch with two cross-service steps (enums are JSON strings).
curl -X POST http://localhost:5070/api/batches \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "worker-mode-demo",
    "source": "Api",
    "failurePolicy": "StopOnFailure",
    "steps": [
      { "stepId": "step-1-invoice", "order": 0, "stepType": "Job",
        "job": { "jobName": "GenerateInvoice", "targetService": "invoicing" } },
      { "stepId": "step-2-ship", "order": 1, "stepType": "Job",
        "job": { "jobName": "ShipOrder", "targetService": "shipping" } }
    ]
  }'
# -> 201 Created (or 409 Conflict if it already exists)

# 2) Trigger a run by name (empty body is fine).
curl -X POST http://localhost:5070/api/batches/by-name/worker-mode-demo/run \
  -H 'Content-Type: application/json' -d '{}'
# -> 202 Accepted, body: { "batchId": "0192..." }
```

Then watch it run live at <http://localhost:5070/dashboard/self> (open the batch run). The two worker
container logs show the cross-service invocations:

* `worker-invoicing` — `GenerateInvoiceJob (invoicing worker): received cross-service invocation from source=ukbatch-server over RabbitMQ`
* `worker-shipping` — `ShipOrderJob (shipping worker): received cross-service invocation from source=ukbatch-server over RabbitMQ`

Track the run's status by id (from the 202 body):

```bash
curl -sS http://localhost:5070/api/batches/<batchId>/status | jq
```

> **Routing-name ↔ `OnService` caveat.** A step's `targetService` is the **routing key** and
> MUST match the worker's `WorkerName` **exactly (Ordinal)**: `invoicing` and `shipping` here. A
> mismatch is **silent** — the message waits forever in the worker's quorum queue, the orchestrator's
> `RequestReplyAsync` times out, and the Workers panel (observability only) won't reveal the cause.
> The step's `jobName` (`GenerateInvoice` / `ShipOrder`) is the job's `[Job(Name = "...")]` value on
> the worker, independent of the routing key.

## 3a. Approval + parallel demo (all three workers)

The `seed-batch.sh` helper also creates and triggers an **`approval-parallel-demo`** batch that
exercises all three workers and every v0.1 workflow shape over the broker:

```text
step 1  ApprovalGate   (allowedRoles:["ops"], onTimeout:"Hold")     ← run pauses here until granted
step 2  ParallelGroup  (joinPolicy:"WaitAll")
          ├─ GenerateInvoice @ invoicing
          └─ ShipOrder       @ shipping                              ← both run concurrently
step 3  Job  SendNotification @ notification
```

When triggered, the run **pauses at the approval gate** — open it live at
<http://localhost:5070/dashboard/self/batches> (the approval node sits amber/awaiting; nothing
downstream fires yet).

### Granting the approval (curl with the `ops` role header)

The gate is `allowedRoles:["ops"]`, so it needs an **authenticated `ops` caller**. The server must run
with **`UKBATCH_DEV_AUTH=true`** (the `docker-compose.yml` sets it on `ukbatch-server`) — that
registers a development-only header-based auth scheme. Then approve via curl, fetching the pending
approval id first:

```bash
# 1) Find the pending approval id.
curl -sS http://localhost:5070/api/approvals | jq
# -> { "items": [ { "approvalId": "0192...", "batchName": "approval-parallel-demo", ... } ], ... }

# 2) Grant it. The X-Dev-Roles: ops header is what authorizes the approval (DevAuth reads it).
curl -X POST "http://localhost:5070/api/approvals/<approvalId>/approve" \
  -H 'Content-Type: application/json' \
  -H 'X-Dev-User: demo-operator' \
  -H 'X-Dev-Roles: ops' \
  -d '{}'
# -> 204 No Content
```

The `seed-batch.sh` output prints the exact approve command with the real id filled in.

The moment the gate is granted, step 2's two parallel nodes (**invoice** + **ship**) go **blue →
green together**, then step 3 (**notify**) runs. The three worker logs show their cross-service
invocations (`received cross-service invocation … over RabbitMQ`).

> **Why curl, not the dashboard button?** The browser dashboard's approve button POSTs **without** the
> role header (it has no header-injection or login flow yet), so against an `["ops"]` gate it would
> 403. Injecting `X-Dev-Roles: ops` from curl is the v0.1 approval path; a real OIDC/Cookie login is a
> v0.2 concern. With **no** DevAuth at all (the default, `UKBATCH_DEV_AUTH` unset) **no** approval
> config is grantable — every request is anonymous, and `["ops"]` → 403, `["*"]` → 403, `[]` → 500
> (fail-safe). That is why the demo opts DevAuth in.

## Intra-job parallelism

The invoicing worker also ships `ReconcileInvoicesJob` — an `IPartitionedJob<InvoiceRow>` that demonstrates
the "SELECT first, then chew through the rows on N concurrent workers" shape. `SourceAsync` streams 12
simulated invoice rows; `ProcessAsync` runs them on **3 concurrent workers** (registered with
`.WithParallelism(3)` + `ItemErrorPolicy.ContinueOnError`, so one bad row counts as a failure instead of
killing the run); and `FinalizeAsync` commits the accumulated results **once** at the end (the unit-of-work
hook). The overlapping `START`/`DONE` log lines in the worker container are the visible proof of the
parallelism — waves of 3, not one at a time.

The asserting harness (below) drives it through a `partitioned-demo` batch (step `ReconcileInvoices` on the
`invoicing` worker). It also exercises the **per-run worker-count override**: triggering with an
`{ "ukbatch.workers": 6 }` parameter raises the worker count for that single run (capped at 128) without
re-registering the job — the worker logs the override and the effective count.

## 4. Durability demo (the broker's headline feature)

Unlike HTTP (where a dead receiver = immediate failure), RabbitMQ **persists** the step's message in a
durable quorum queue until a consumer acks it. Stop a worker, trigger, and watch the message wait:

```bash
# Stop the shipping worker. Leave everything else running.
docker compose stop worker-shipping
```

The Workers panel now shows `shipping` flipping to **Offline** (the worker sent a best-effort `Offline`
heartbeat on graceful shutdown; otherwise it would age out after the ~45s TTL).

```bash
# Trigger another run.
samples/Sample.WorkerMode/seed-batch.sh
```

Step 1 (`GenerateInvoice`) completes on the still-running invoicing worker. Step 2 (`ShipOrder`)'s
message is published to `ukbatch.service.shipping` and **waits** there — confirm via the RabbitMQ
management UI (<http://localhost:15672>, `guest`/`guest`) under **Queues**: `ukbatch.service.shipping`
shows **Ready = 1**. The orchestrator's `RequestReplyAsync` blocks up to its request timeout (30s).

```bash
# Restart the shipping worker WITHIN the timeout window.
docker compose start worker-shipping
```

It reconnects, consumes the queued message, runs `ShipOrder`, and replies via direct-reply-to — step 2
completes and the batch finishes. The Workers panel shows `shipping` going Offline → Online again.

> If you exceed the 30s timeout, step 2 fails with a timeout and the batch run is marked failed; the
> message is still consumed when the worker restarts. Restart within 30s to see the happy path.

## Automated end-to-end assertions

`seed-batch.sh` is "create + trigger + read with your eyes". Its asserting sibling, `e2e-assert.sh`, drives
the same live Compose stack over the REST API, **polls each run to a terminal state, and fails hard (exit 1)
on any mismatch** — the value being the real images + real broker + real network + real Postgres path that
in-process unit tests cannot exercise. With the stack up, run:

```bash
docker compose up -d --build --wait
bash samples/Sample.WorkerMode/e2e-assert.sh
```

It covers: stack health and the 3 workers reporting Online; a simple sequential cross-service run; the
approval + parallel flow (asserting the gate really holds before granting, then all three cross-service
executions complete); RabbitMQ durability (stop a worker, confirm its step waits in the durable queue,
restart, confirm completion); a compensation path (a deliberately-failing job drives the `OnFailure` branch);
partitioned fan-out including the `ukbatch.workers` per-run override; and Postgres state durability across a
server restart (definitions and completed history survive — within the v0.1 boundary of durable record, not
workflow resume).

## 5. Tear down

```bash
docker compose down            # stop + remove containers (Postgres data lives in the container only)
docker compose down -v         # also remove volumes (none declared here, but clears anonymous ones)
```

## Inspecting the broker

Open <http://localhost:15672> (`guest`/`guest`) → **Queues**:

* `ukbatch.service.invoicing` — the invoicing worker's durable **quorum** service queue
* `ukbatch.service.shipping` — the shipping worker's durable quorum service queue
* `ukbatch.service.notification` — the notification worker's durable quorum service queue
* `ukbatch.service.ukbatch-server` — the server's own service queue (declared because it sets
  `ThisServiceName=ukbatch-server`; unused for sending)
* `ukbatch.dlq` — the dead-letter queue (messages land here after exceeding the delivery limit)

## How the wiring maps to code

* **Server** (`UKBatch.Server`, generic image): env-var driven — `UKBATCH_STORAGE=ef-pg`,
  `UKBATCH_TRANSPORT=rabbitmq`, `UKBATCH_ENABLE_DASHBOARD=true`. Registers no jobs/batches; definitions
  come from REST/the dashboard wizard and persist in Postgres.
* **Workers** (`Sample.WorkerMode.Invoicing` / `.Shipping` / `.Notification`):
  `AddUKBatchAspNetCore(b => { b.AddJob<…>(); b.UseWorkerMode(w => { w.WorkerName = "invoicing";
  w.ServerUrl = "http://ukbatch-server:8080"; … }); })` + `AddUKBatchRabbitMqTransport()`. The
  heartbeat goes over **HTTP** to the server's `/api/workers/beat`; the cross-service job dispatch goes
  over the **broker**.
* **Approval auth** (server, opt-in): `UKBATCH_DEV_AUTH=true` registers a development-only header-based
  auth scheme so the `approval-parallel-demo` gate (`["ops"]`) can be granted via curl with
  `X-Dev-User` + `X-Dev-Roles: ops`. Default off → unchanged production posture (no scheme = every
  request anonymous). Full OIDC/Cookie login is a v0.2 concern.

## Production checklist

* **Do not use `guest`/`guest` or the demo Postgres password.** Provision dedicated credentials and set
  them via env vars (`UKBatch__Transport__RabbitMQ__UserName` / `…__Password`, and a real
  `UKBATCH_STORAGE_CONNECTION`).
* **Enable TLS** on the broker (`amqps://`) and front the server with HTTPS. There is no application-level
  HMAC in the RabbitMQ transport — auth + confidentiality live at the broker layer.
* **Worker → server auth is not yet enforced** (v0.1): `/api/workers/*` is auth-agnostic. If you secure
  `/api`, the heartbeat 401s and the panel goes dark — but **dispatch is unaffected** (it rides the
  broker, not the heartbeat).
* Scale a worker tier by running multiple instances of the same worker image (each consumes the same
  service queue) rather than raising per-channel concurrency.
```
