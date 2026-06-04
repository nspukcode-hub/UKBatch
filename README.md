# UKBatch

Lite, pluggable batch and job orchestration for .NET microservices.

> Status: 0.1.0-alpha — under active development. Not yet on NuGet.

## What it is

UKBatch is a NuGet package family that lets you orchestrate jobs and batches across one service or many services with minimal code. Add a NuGet, register your jobs, and either embed the dashboard in your app or run the standalone Docker server for centralized management.

## Highlights

- **One-line integration** — `services.AddUKBatch()` + `app.UseUKBatchDashboard()`
- **Hybrid deployment** — Embedded library or standalone Docker server + workers. Same NuGet, different config.
- **Pluggable everything** — Storage (InMemory / SQL / Redis), Transport (InProcess / HTTP / RabbitMQ / Kafka), UI (Blazor Server or REST + your own UI)
- **Real workflow patterns** — Sequential jobs, parallel fan-out/fan-in, manual approval gates, cross-service workflows
- **Data-parallel jobs** — Fetch + N-worker process pattern as a first-class primitive (`IPartitionedJob<T>`)
- **Embedded dashboard** — Blazor Server UI for monitoring, triggering, and building batches visually
- **SOLID + clean architecture** — Built for long-term maintainability

## Quick start (embedded)

```csharp
// Program.cs
builder.Services.AddUKBatch(opts =>
{
    opts.UseInMemoryStorage();
    opts.UseInProcessTransport();
    opts.DiscoverJobsFromAssembly(typeof(Program).Assembly);
});

var app = builder.Build();
app.UseUKBatchApi();
app.UseUKBatchDashboard();
app.Run();
```

```csharp
[Job(Name = "send-welcome-emails", Schedule = "0 9 * * *", MaxRetries = 3)]
public class SendWelcomeEmailsJob : IJob
{
    public async Task ExecuteAsync(JobContext ctx, CancellationToken ct)
    {
        // your work
    }
}
```

Open `/ukbatch` in your browser. Done.

## Quick start (server + workers)

```bash
docker run -d -p 8080:80 \
  -e UKBATCH_STORAGE_CONNSTR="Host=postgres;Database=ukbatch" \
  ukbatch/server:0.1.0-alpha
```

```csharp
// In each microservice
builder.Services.AddUKBatch(opts =>
{
    opts.UseWorkerMode(w => w.ServerUrl = "http://ukbatch-server:8080");
    opts.AddJob<MyJob>();
});
```

Dashboard at `http://ukbatch-server:8080/ukbatch`. Build and run batches across services from there.

## Packages

| Package | Purpose |
|---|---|
| `UKBatch.Abstractions` | Interfaces only, zero-dep |
| `UKBatch.Core` | Runtime, scheduler, in-memory store, in-process transport |
| `UKBatch.AspNetCore` | ASP.NET Core integration |
| `UKBatch.Worker` | Worker-mode helper |
| `UKBatch.Api` | REST endpoints + OpenAPI + SignalR hub |
| `UKBatch.Dashboard` | Blazor Server embedded dashboard |
| `UKBatch.Transport.Http` | Cross-service HTTP transport (queue-free) |
| `UKBatch.Transport.RabbitMQ` | RabbitMQ transport adapter |
| `UKBatch.Transport.Kafka` | Kafka transport adapter |
| `UKBatch.Transport.AzureServiceBus` | Azure Service Bus adapter |
| `UKBatch.Storage.EntityFrameworkCore` | SQL store (SqlServer / PostgreSQL / SQLite) |
| `UKBatch.Storage.Redis` | Redis store |

Plus the Docker image: `ukbatch/server:0.1.0-alpha`.

## License

[MIT](LICENSE)
