# UKBatch.Core

The runtime for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a lightweight, pluggable batch/job orchestration library for .NET 8 and .NET 10. This package contains the dispatcher, the cron scheduler (via [Cronos](https://github.com/HangfireIO/Cronos)), the job/batch executors, the in-memory stores, and the in-process transport. It is everything you need to run jobs inside a single process; adapter packages swap in persistence and cross-service transport without code changes.

> **Status:** part of the UKBatch 0.1.0-alpha package family.

## Install

```bash
dotnet add package UKBatch.Core
```

## Quick start

In a console or generic-host application, register the runtime with `AddUKBatch` and describe your jobs and batches in the builder:

```csharp
using UKBatch;

services.AddUKBatch(b =>
{
    // A single job, addressed by an explicit name.
    b.AddJob<MyJob>().Named("MyJob");

    // A sequential batch: StepA, then StepB.
    b.AddBatch("pipeline", x => x
        .RunJob<StepA>()
        .ThenRunJob<StepB>());

    // Or discover [Job]-decorated types by scanning assemblies:
    // b.ScanAssemblies(typeof(Program).Assembly);
});
```

`MyJob`, `StepA`, and `StepB` are classes implementing `IJob` (from UKBatch.Abstractions). Resolve `IJobRunner` from DI to trigger a job or batch at runtime.

## Defaults

Out of the box `AddUKBatch` wires the **in-memory store** and the **in-process transport** — no extra calls are required to get running. Swap either by adding an adapter package: **UKBatch.Storage.EntityFrameworkCore** for PostgreSQL/SQLite persistence, **UKBatch.Transport.Http** or **UKBatch.Transport.RabbitMQ** for cross-service messaging. The same job and batch code keeps working; only where state lives and how services talk changes.

## Workflows

Batches compose real patterns, all expressed through the fluent builder:

- **Sequential** — `RunJob<A>().ThenRunJob<B>()`.
- **Parallel fan-out / fan-in** — `ThenInParallel(p => p.RunJob<A>().RunJob<B>().JoinPolicy(ParallelJoinPolicy.WaitAll))`.
- **Approval gate** — `ThenWaitForApproval(title: "Confirm", roles: ["ops"], timeout: TimeSpan.FromMinutes(30), onTimeout: ApprovalTimeoutAction.Hold)` pauses the batch until a human approves or rejects.
- **Compensation** — `OnFailure(f => f.RunJob<Rollback>()).FailurePolicy(BatchFailurePolicy.Compensate)`.

## Partitioned jobs

For data-parallel work — "fetch a set of items, then process them on N workers" — implement `IPartitionedJob<TItem>`. The runtime owns the producer/consumer plumbing (a bounded channel plus N consumer tasks); you declare only the source stream and the per-item work:

```csharp
using UKBatch.Abstractions.Jobs;

public sealed class ReconcileInvoicesJob : IPartitionedJob<int>
{
    // Stream the items to process. Yield lazily so the bounded channel applies backpressure.
    public async IAsyncEnumerable<int> SourceAsync(JobContext context, CancellationToken ct)
    {
        context.Progress.SetTotal(100);          // drives a live x/100 progress counter
        for (var id = 1; id <= 100; id++)
            yield return id;
    }

    // Runs on N concurrent workers — MUST be thread-safe.
    public Task ProcessAsync(int id, JobContext context, CancellationToken ct) =>
        ReconcileAsync(id, ct);

    // Optional commit hook: runs exactly once after every item, single-threaded.
    // Skipped on a fail-fast abort or cancellation; under ContinueOnError it commits the subset that succeeded.
    public Task FinalizeAsync(JobContext context, CancellationToken ct) =>
        SaveResultsAsync(ct);
}
```

Register it with a worker count and a per-item error policy:

```csharp
b.AddPartitionedJob<ReconcileInvoicesJob, int>()
    .Named("ReconcileInvoices")
    .WithParallelism(4)
    .WithItemErrorPolicy(ItemErrorPolicy.ContinueOnError);
```

The worker count can be overridden per run by passing the trigger parameter `ukbatch.workers` (an invalid value falls back to the configured parallelism with a warning, and the effective count is capped at 128).

## When to use it

- **ASP.NET Core apps** should reference **UKBatch.AspNetCore** instead — it depends on this package and adds host integration, `HttpContext`-aware trigger enrichment, and a readiness health check.
- **Plain hosts and console apps** reference **UKBatch.Core** directly and register with `services.AddUKBatch(...)`.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [UKBatch on GitHub](https://github.com/nspukcode-hub/UKBatch).
