# UKBatch.Transport.Http

A broker-free HTTP transport adapter for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a lightweight, pluggable batch/job orchestration library for .NET 8 and .NET 10. It implements `ITransport` over HMAC-signed REST so a batch can dispatch a step to a different microservice as easily as a local step, with no message broker to run.

> **Status:** part of the UKBatch 0.1.0-alpha package family. Same `JobMessage` / `JobResult` envelope as `UKBatch.Transport.RabbitMQ` — a different wire.

## Install

```bash
dotnet add package UKBatch.Transport.Http
```

## Quick start

Both sides register the transport; the secret and service registry bind from the `UKBatch:Transport:Http` configuration section.

**Orchestrator (sender):**

```csharp
using UKBatch.AspNetCore;
using UKBatch.Transport.Http;

builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o => o.ThisServiceName = "orchestrator");
    b.AddBatch("invoice-pipeline", batch => batch
        .RunJob<PrepareOrderJob>()                                       // local
        .ThenRunJob("InvoiceProcessing", step => step.OnService("billing-worker")));  // remote
});

builder.Services.AddUKBatchHttpTransport();   // binds UKBatch:Transport:Http
```

**Worker (receiver):**

```csharp
builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o => o.ThisServiceName = "billing-worker");
    b.AddJob<InvoiceProcessingJob>().Named("InvoiceProcessing");
});

builder.Services.AddUKBatchHttpTransport();

var app = builder.Build();
app.MapUKBatchHttpTransport();   // exposes /ukbatch/internal/jobs/{publish,poll,invoke}
app.Run();
```

The sender does **not** reference the worker's job assembly — only the job NAME is shared. The full sample is under [`samples/Sample.CrossServiceHttp`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.CrossServiceHttp).

## Configuration

```jsonc
{
  "UKBatch": {
    "Transport": {
      "Http": {
        "SharedSecret": "<32+ bytes — env var / Key Vault in production>",
        "Services": {
          "billing-worker": { "BaseUrl": "http://billing-worker:5150" }
        }
      }
    }
  }
}
```

`SharedSecret` is the HMAC key and is required whenever `Services` is non-empty (the validator enforces this). Receiver-only nodes have no outbound targets and leave `Services` empty.

## HMAC SHA256 auth

Every request carries `X-UKBatch-Signature`, `X-UKBatch-Timestamp`, and `X-UKBatch-Nonce`. The signature is HMAC-SHA256 over a strict canonical envelope (method, canonical path, timestamp, nonce, body hash) that sender and receiver compute identically. Replay is blocked by an LRU nonce cache plus a clock-skew window (`MaxClockSkew`, default 5 min). Signature mismatch / missing header / nonce replay all return `401 ukbatch:transport-auth-failed` (no information leak); clock-skew failures return `401 ukbatch:transport-clock-skew` so legitimate NTP drift is diagnosable.

## Resilience and limits

Each per-service `HttpClient` carries one Polly v8 pipeline (via `Microsoft.Extensions.Http.Resilience`): an outer wall-clock timeout, retry (default `[2s, 5s, 15s]` + jitter), a circuit breaker (5 failures per 30s → open 30s → half-open probe), and a per-attempt timeout. 4xx does **not** retry (a caller error wastes the budget); only `HttpRequestException`, 5xx, 408, and 503 do. Request bodies are capped at `MaxBodyBytes` (1 MB default) to bound HMAC body-hash CPU cost — keep your worker's Kestrel `MaxRequestBodySize` at or above it.

## Critical notes

- **Service identity is required for outbound steps.** Set `UKBatchOptions.ThisServiceName` (or env var `UKBATCH_SERVICE_NAME`); a cross-service step without it fails fast at dispatch with an actionable error.
- **The receiver mount path is fixed** at `/ukbatch/internal/jobs/*` — not caller-configurable, so service-to-service discovery is unambiguous.
- **`Cache-Control: no-store`** is set on every receiver response (even on a handler throw) so intermediaries never cache long-poll responses.
- **`SharedSecret` in plaintext `appsettings.json` is for dev/samples only** — source it from an env var or a secret store in production.

## When to use it

Choose this transport for cross-service dispatch when you want point-to-point request/reply without standing up a broker. If you need a stopped worker's message to wait durably until it restarts, use `UKBatch.Transport.RabbitMQ` instead — same envelope, broker-backed.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [nspukcode-hub.github.io/UKBatch](https://nspukcode-hub.github.io/UKBatch/) · [GitHub](https://github.com/nspukcode-hub/UKBatch).
