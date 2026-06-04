# UKBatch.Transport.Http

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

HTTP transport adapter for the [UKBatch](https://github.com/nspukcode-hub/UKBatch) batch / job orchestration ecosystem. Implements `ITransport` over HMAC-signed REST so a batch can dispatch a step to a different microservice as easily as a local step.

> **v0.1.0-alpha** — broker-free cross-service transport. A RabbitMQ adapter (`UKBatch.Transport.RabbitMQ`) is also available; Kafka / Azure Service Bus adapters are planned.

## What is UKBatch.Transport.Http?

`UKBatch.Transport.Http` lets two (or more) UKBatch-hosting microservices talk to each other without a broker. The orchestrator side calls `_transport.PublishAsync(...)` / `RequestReplyAsync(...)`; the worker side mounts three internal endpoints under `/ukbatch/internal/jobs/*` that accept the same `JobMessage` envelope and return `JobResult`. Authentication is HMAC SHA256 over a strict canonical envelope (method + path + timestamp + nonce + body hash); replay is blocked by an LRU nonce cache + clock-skew window.

Position in the package family:

```
UKBatch.Abstractions   (zero-dep interfaces)
UKBatch.Core           (runtime, scheduler, in-memory stores, in-process transport)
UKBatch.AspNetCore     (IHostedService, DI integration)
UKBatch.Api            (REST + OpenAPI + SignalR)
UKBatch.Dashboard      (Blazor Server UI)
UKBatch.Transport.Http (cross-service ITransport)   ← this package
UKBatch.Storage.*      (EF Core / Redis)
```

## Install

```bash
dotnet add package UKBatch.Abstractions
dotnet add package UKBatch.Core
dotnet add package UKBatch.AspNetCore
dotnet add package UKBatch.Transport.Http
```

## Quickstart

### Orchestrator (sender side)

```csharp
using UKBatch.AspNetCore;
using UKBatch.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o => o.ThisServiceName = "orchestrator");

    b.AddBatch("invoice-pipeline", batch =>
    {
        batch.RunJob("InvoiceProcessing", step =>
        {
            step.OnService("billing-worker");
            step.WithTimeout(60);
        });
    });
});

// Replace the default InProcessTransport singleton with HttpTransport.
builder.Services.AddUKBatchHttpTransport(opts =>
{
    opts.SharedSecret = builder.Configuration["UKBatch:Transport:Http:SharedSecret"]!;
    opts.Services["billing-worker"] = new ServiceEndpoint
    {
        BaseUrl = new Uri("http://billing-worker:5150/")
    };
});

var app = builder.Build();
app.Run();
```

### Worker (receiver side)

```csharp
using UKBatch.AspNetCore;
using UKBatch.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o => o.ThisServiceName = "billing-worker");
    b.AddJob<InvoiceProcessingJob>().Named("InvoiceProcessing");
});

builder.Services.AddUKBatchHttpTransport(opts =>
{
    opts.SharedSecret = builder.Configuration["UKBatch:Transport:Http:SharedSecret"]!;
    // No Services{} on the worker — receiver-only nodes have no outbound targets.
});

var app = builder.Build();
app.MapUKBatchHttpTransport();   // exposes /ukbatch/internal/jobs/publish + /poll + /invoke

app.Run();
```

The worker's full sample lives under `samples/Sample.CrossServiceHttp/`.

## Wire surface

All three endpoints are fixed-path under `/ukbatch/internal/jobs/`:

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/ukbatch/internal/jobs/publish` | Fire-and-forget message publish |
| `GET`  | `/ukbatch/internal/jobs/poll?topic={t}&waitMs={ms}` | Long-poll subscribe |
| `POST` | `/ukbatch/internal/jobs/invoke` | Synchronous request/reply (used by cross-service batch steps) |

The path prefix is **not** caller-configurable — every UKBatch worker exposes the same mount point so service-to-service discovery is unambiguous.

## HMAC SHA256 auth

Every request carries three headers:

```
X-UKBatch-Signature: <base64(HMACSHA256(secret, canonical))>
X-UKBatch-Timestamp: <unix epoch ms>
X-UKBatch-Nonce:     <base64url(16 random bytes)>
```

Canonical envelope (newline-delimited UTF-8, sender and receiver MUST compute identically):

```
{HTTP-METHOD}\n
{canonical-path}\n
{timestamp-ms}\n
{nonce}\n
{base64(sha256(body))}
```

Canonical path rules (strict normalization):

1. Trailing slash stripped (unless path is exactly `/`).
2. Query parameters sorted by key (ordinal); values sorted within key.
3. Percent-encoded per RFC 3986 via `Uri.EscapeDataString` (`%20` for space, NOT `+`).
4. No trailing `?` when the query is empty.

**Spoof-resistance contract:** signature mismatch, missing header, AND nonce replay ALL return `401 ukbatch:transport-auth-failed` (OWASP fold — no information leak). Clock-skew failures return `401 ukbatch:transport-clock-skew` separately because legitimate NTP drift is a real ops concern.

## Resilience (Polly v8)

Each per-service named `HttpClient` carries one resilience pipeline (configured via `Microsoft.Extensions.Http.Resilience`). Composition from outer to inner:

```
Caller invocation
    │
    ▼
Outer wall-clock timeout    (= options.DefaultRequestTimeout, across retries)
    │
    ▼
Retry strategy              (default [2s, 5s, 15s] + jitter)
    │
    ▼
Circuit breaker             (5 fails per 30s window → open for 30s → half-open probe)
    │
    ▼
Per-attempt timeout         (= options.DefaultRequestTimeout)
    │
    ▼
HttpClient.Timeout          (INFINITE — Polly authoritative)
```

**4xx does NOT retry.** Only `HttpRequestException` + 5xx + 408 + 503. 4xx is a caller error (bad signature, unknown job) and retrying wastes the budget.

## Cross-service step usage

In a `BatchBuilder` chain, prefix any step with `.OnService("worker-name")`:

```csharp
b.AddBatch("invoice-pipeline", batch =>
{
    batch.RunJob<PrepareOrderJob>()                                // local
         .ThenRunJob("InvoiceProcessing", step =>                  // cross-service
         {
             step.OnService("billing-worker");
             step.WithTimeout(120);
         })
         .ThenRunJob<NotifyCustomerJob>();                         // local again
});
```

The string-name overload (`.ThenRunJob("InvoiceProcessing", ...)`) exists so the orchestrator does NOT need to reference the worker's job assembly — only the job NAME is shared.

**Service identity (`UKBatchOptions.ThisServiceName`):** required if any batch contains an outbound cross-service step. Resolution chain:

1. `UKBatchOptions.ThisServiceName` (from `appsettings.json` `UKBatch:ThisServiceName` or `builder.Configure(o => o.ThisServiceName = "...")`).
2. Env var `UKBATCH_SERVICE_NAME`.
3. `Assembly.GetEntryAssembly()?.GetName().Name` (last-resort fallback).

Missing identity on a cross-service step fails fast at dispatch time with an actionable `InvalidOperationException` naming both config paths.

## Configuration options

`HttpTransportOptions` is bound from the `UKBatch:Transport:Http` section. Defaults:

| Option | Default | Purpose |
|---|---|---|
| `Services` | empty dict | Per-service endpoint registry (receiver-only nodes may leave empty) |
| `SharedSecret` | empty | HMAC secret — REQUIRED if `Services` non-empty (validator enforces) |
| `DefaultRequestTimeout` | `30s` | Per-request wall-clock timeout |
| `LongPollMaxWait` | `30s` | Server-side cap on `/poll` hold duration |
| `RetryDelays` | `null` (= [2s, 5s, 15s] + jitter) | Polly retry schedule |
| `CircuitBreakerThreshold` | `5` | Failures within window before breaking |
| `CircuitBreakerWindow` | `30s` | Sampling + open duration |
| `MaxClockSkew` | `300s` | HMAC timestamp tolerance |
| `NonceCacheCapacity` | `1024` | LRU size for anti-replay |
| `MessageIdCacheCapacity` | `4096` | LRU size for receiver-side message dedupe |
| `MaxBodyBytes` | `1 MB` | Request body cap |

## ProblemDetails error map

| Type URI | Status | When |
|---|---|---|
| `ukbatch:transport-auth-failed` | 401 | Signature mismatch / missing header / nonce replay (OWASP fold) |
| `ukbatch:transport-clock-skew` | 401 | Timestamp outside `MaxClockSkew` window |
| `ukbatch:transport-unknown-service` | 400 | Sender supplied a service name not in `Services` |

## Operator caveats

- **`Cache-Control: no-store`** is set automatically on every receiver response — intermediaries (nginx, CDN, browser) MUST NOT cache long-poll responses. The middleware sets the header BEFORE the handler runs, so even handler-side throws still emit it.
- **Kestrel body limits:** if you send `JobMessage` payloads near `MaxBodyBytes` (1 MB default), confirm your worker's `Kestrel:Limits:MaxRequestBodySize` is configured ≥ same value. Default Kestrel is 30 MB; UKBatch caps at 1 MB internally to bound HMAC body-hash CPU cost.
- **`SharedSecret` provisioning:** plaintext `appsettings.json` is acceptable for dev / samples only. Production deployments MUST source from env var, Azure Key Vault, AWS Secrets Manager, etc. Validator does not inspect entropy; choose 32+ bytes.
- **Clock sync:** `MaxClockSkew` defaults to 5 minutes (NTP-realistic). If you run worker / orchestrator on clock-drifting hosts, monitor `ukbatch:transport-clock-skew` rate — that's the diagnostic surface.

## Limitations & roadmap

- **v0.1-alpha:** static service registry only (no Consul / Eureka / DNS-SRV). Discovery hook `IServiceDiscovery` is declared but not registered.
- **Planned:** key rotation (rolling secret), header-based service discovery, OpenTelemetry instrumentation, persistent MessageId dedupe (currently in-memory only).
- **Alternative transport:** `UKBatch.Transport.RabbitMQ` provides an AMQP `ITransport` — same `JobMessage` envelope, different wire.

## License & support

MIT licensed. Source code, samples, issue tracker:

- GitHub: <https://github.com/nspukcode-hub/UKBatch>

Contributions welcome.
