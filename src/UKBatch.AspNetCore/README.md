# UKBatch.AspNetCore

ASP.NET Core integration for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a lightweight, pluggable batch/job orchestration library for .NET 8 and .NET 10.

This package adds three things to a UKBatch host running inside an ASP.NET Core application:

1. **`HttpContext` enricher** — `JobExecution.TriggeredBy` is populated automatically from `HttpContext.User.Identity.Name` (with a `sub`-claim fallback) when a job is triggered from inside a request handler.
2. **W3C trace propagation** — the ambient `Activity` is captured at trigger time and can be restored inside the job via `JobContext.RestoreRequestActivity()` so logs and child spans correlate with the originating HTTP request.
3. **Readiness `IHealthCheck`** — registered under the name `"ukbatch"` with tags `["ukbatch", "ready"]`; signals `Healthy` once the runtime has started.

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddUKBatchAspNetCore(b =>
{
    b.AddJob<MyJob>();
});
var app = builder.Build();
app.MapHealthChecks("/healthz");
app.Run();
```

## REQUIRED — opt in to trace correlation via `RestoreRequestActivity`

Without this single line at the top of your `IJob.ExecuteAsync`, the runtime cannot correlate logs and child spans with the originating HTTP request — trace propagation is opt-in by design.

```csharp
public sealed class MyJob : IJob
{
    public async Task ExecuteAsync(JobContext ctx, CancellationToken ct)
    {
        using var _ = ctx.RestoreRequestActivity();   // <-- REQUIRED for trace correlation
        ctx.Logger.LogInformation("triggered by {TriggeredBy}", ctx.TriggeredBy);
        await DoWorkAsync(ct);
    }
}
```

## Triggering from a request handler

Resolve `IJobRunner`, `IJobTriggerContext`, and `IJobTraceContext` from DI and call `TriggerWithRequestContextAsync`:

```csharp
app.MapGet("/trigger/hello",
    async (IJobRunner runner, IJobTriggerContext idCtx, IJobTraceContext traceCtx, CancellationToken ct) =>
    {
        var execution = await runner.TriggerWithRequestContextAsync(
            idCtx, traceCtx, jobName: "MyJob", JobParameters.Empty, ct);
        return Results.Ok(new { execution.ExecutionId, execution.TriggeredBy });
    });
```

The same shape exists for batches via `TriggerBatchWithRequestContextAsync`.

## Health check

`AddUKBatchAspNetCore` registers a readiness signal:

- `Healthy` after `IHostApplicationLifetime.ApplicationStarted` fires.
- `Unhealthy` during startup.

It is tagged `"ready"` (not `"live"`) — wire it as a Kubernetes readiness probe.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [UKBatch on GitHub](https://github.com/nspukcode-hub/UKBatch).
