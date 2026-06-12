# Sample.SimpleJob

Minimal ASP.NET Core host demonstrating `UKBatch.AspNetCore`. Three jobs:

- `HelloJob` — a trivial `IJob` that logs `TriggeredBy`.
- `ScheduledHeartbeatJob` — scheduled every 30s via Cron (`*/30 * * * * *`).
- `ItemProcessorJob` — an `IPartitionedJob<int>` (4 workers, item count via `?count=N`).

## REQUIRED — `ctx.RestoreRequestActivity()` opt-in

Every `IJob.ExecuteAsync` in this sample opens with `using var _ = ctx.RestoreRequestActivity();`. This is REQUIRED for trace correlation between the HTTP request and the asynchronous job execution. Without it, logs and child spans do NOT carry the request's W3C `traceparent`.

```csharp
public Task ExecuteAsync(JobContext ctx, CancellationToken ct)
{
    using var _ = ctx.RestoreRequestActivity();   // <-- REQUIRED
    ctx.Logger.LogInformation("...");
    return Task.CompletedTask;
}
```

## Run

```bash
dotnet run --project samples/Sample.SimpleJob -f net10.0
```

`-f` is required because the sample multi-targets `net10.0;net8.0`. The launch profile pins
`http://localhost:5001` and the `Development` environment (the development-only auth scheme
refuses to start in `Production` by design).

## Endpoints

```bash
# Trigger HelloJob with an identity header
curl -H "X-Dev-User: alice" http://localhost:5001/trigger/hello
# {"executionId":"<id>","triggeredBy":"alice","status":"Pending"}

# Trigger ItemProcessorJob with 200 items
curl -H "X-Dev-User: alice" "http://localhost:5001/trigger/items?count=200"

# Trigger ScheduledHeartbeatJob manually (the scheduler also fires it every 30s)
curl -H "X-Dev-User: alice" http://localhost:5001/trigger/scheduled

# Inspect an execution by id
curl http://localhost:5001/status/<executionId>

# Readiness health probe
curl http://localhost:5001/healthz
```

DevAuth is for local development ONLY. Production hosts should plug in cookies, JWT, or any other ASP.NET Core authentication scheme.
