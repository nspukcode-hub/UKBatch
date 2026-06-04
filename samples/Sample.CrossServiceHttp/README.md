# Sample.CrossServiceHttp — 2-process demo

End-to-end demo of `UKBatch.Transport.Http` — a 3-step batch where step 2 crosses the wire
to another OS process via HMAC-signed HTTP POST. Step 1 + step 3 run locally on the
**orchestrator**; step 2 (`InvoiceProcessing`) runs on the **worker**.

## Two-terminal recipe (recommended)

**Terminal 1** — worker:

```bash
cd samples/Sample.CrossServiceHttp.Worker
dotnet run --urls http://localhost:5150
```

**Terminal 2** — orchestrator:

```bash
cd samples/Sample.CrossServiceHttp.Orchestrator
dotnet run --urls http://localhost:5050
```

Then trigger the batch:

```bash
curl -X POST http://localhost:5050/api/batches/by-name/cross-service-demo/run \
     -H "Content-Type: application/json" \
     -d '{}'
```

Open <http://localhost:5050/dashboard> to watch the batch run live.

## macOS gotcha

macOS 12+ holds port 5000 for Control Center / AirPlay Receiver (`com.apple.controlcenter`).
This sample uses **5050** (orchestrator) and **5150** (worker) to avoid the conflict. If
either port is also taken on your machine, override via `--urls http://localhost:<port>`.

## Verifying the cross-service hop

Watch the two terminals while the batch runs:

* **Step 1** `PrepareOrderJob` — orchestrator terminal logs `generated orderId=…`
* **Step 2** `InvoiceProcessing` — **worker** terminal logs `processing orderId=… from source=orchestrator`
* **Step 3** `FinalizeOrderJob` — orchestrator terminal logs `cross-service step completed; finalizing`

The dashboard's batch detail page annotates step 2 with `TargetService=billing-worker`.

## HMAC tampering test

While the orchestrator + worker are running with the **same** `SharedSecret`, run a tampered
request directly against the worker (no signature):

```bash
curl -i -X POST http://localhost:5150/ukbatch/internal/jobs/publish \
     -H "Content-Type: application/json" \
     -d '{"messageId":"fake"}'
# → 401 Unauthorized, ProblemDetails type ukbatch:transport-auth-failed
```

Now change `appsettings.json` `SharedSecret` on the worker to a DIFFERENT value, restart the
worker, and re-trigger the batch from the orchestrator → step 2 fails with `BatchStepFailureException`
(401 propagates through the orchestrator's `RequestReplyAsync`).

## Kill-worker recovery test

1. During an active batch, kill the worker process (Ctrl+C in Terminal 1).
2. The orchestrator's Polly circuit breaker opens after `CircuitBreakerThreshold` consecutive failures.
3. Restart the worker (`dotnet run --urls http://localhost:5150`).
4. After `CircuitBreakerWindow` elapses, trigger a NEW batch — the half-open probe succeeds and
   the circuit closes back. Subsequent steps complete normally.

## Docker compose alternative

A `docker-compose.yml` lives next to this README. It expects per-service orchestrator + worker
Dockerfiles that this HTTP-transport sample does not ship, so the two-terminal `dotnet run` recipe
above is the canonical path. (For a fully containerized server + workers stack, see `Sample.WorkerMode`.)

## Production checklist

* Replace the `DEV-SECRET-NOT-FOR-PRODUCTION-…` literal in both `appsettings.json` files with a
  **32+ byte random secret** sourced from a secret manager (Azure Key Vault, AWS Secrets Manager,
  HashiCorp Vault). Setting it via env var `UKBatch__Transport__Http__SharedSecret=…` is the
  recommended container path.
* Deploy behind TLS — the HMAC envelope protects integrity + identity, but the payload is still
  plaintext over the wire. Reverse-proxy termination (nginx, AWS ALB) is the v0.1 expectation.
* Ensure clock sync (NTP / chrony) on all participating hosts. The default `MaxClockSkew` of 5
  minutes is generous; drop to 30s once NTP is healthy.
