# UKBatch.Abstractions

The zero-dependency contract surface for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a lightweight, pluggable batch/job orchestration library for .NET 8 and .NET 10. This package is interfaces, attributes, and DTOs only: `IJob`, `IPartitionedJob<TItem>`, `[Job]`, `JobContext`, `JobParameters`, and the storage/transport contracts every other `UKBatch.*` package builds on.

Reference it from a shared contracts assembly when you want to *define* jobs without pulling in the runtime — for example a library project that several services consume.

> **Status:** part of the UKBatch 0.1.0-alpha package family.

## Install

```bash
dotnet add package UKBatch.Abstractions
```

## Quick example

Declare a job against the contract. Nothing here executes — the type is just metadata plus a method the runtime will call later.

```csharp
using UKBatch.Abstractions.Jobs;

[Job(Name = "DailyReport", Schedule = "0 9 * * *", MaxRetries = 3)]
public sealed class DailyReportJob : IJob
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation("Building the daily report…");
        return Task.CompletedTask;
    }
}
```

The `[Job(...)]` attribute carries optional `Name`, `Schedule` (cron), `MaxRetries`, `TimeoutSeconds`, and `Tags`. It is consumed by attribute-based discovery at host startup — see UKBatch.Core.

## This package runs nothing on its own

`UKBatch.Abstractions` has no dispatcher, scheduler, or stores. To actually run a job you need the runtime in **UKBatch.Core** plus a host package:

- **ASP.NET Core app** → reference **UKBatch.AspNetCore** (brings Core) and register jobs with `builder.AddUKBatchAspNetCore(...)`.
- **Console / generic host** → reference **UKBatch.Core** directly and register with `services.AddUKBatch(...)`.

## When to use it

Reference this package directly only from **shared contract libraries** — assemblies that define jobs or work with UKBatch DTOs but must stay free of the runtime and its transitive dependencies. Application and worker projects get these contracts transitively through UKBatch.Core.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [UKBatch on GitHub](https://github.com/nspukcode-hub/UKBatch).
