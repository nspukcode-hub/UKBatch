using Sample.WorkerMode.Shipping.Jobs;
using UKBatch.AspNetCore;
using UKBatch.Transport.RabbitMQ;
using UKBatch.Worker;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.AddUKBatchAspNetCore(b =>
{
    // Worker-local runtime state (execution history) — in-memory is fine; durable state lives on the
    // SERVER (EF Core storage). A worker only needs an IJobStore to run its own job executions.
    b.UseInMemoryStorage();
    b.Configure(o => o.MaxDegreeOfParallelism = 4);

    // The job's routing name is "ShipOrder" via [Job(Name = "ShipOrder")]; the server's batch
    // definition references it as the step JobName + OnService("shipping").
    b.AddJob<ShipOrderJob>();
    b.AddJob<CancelShipmentJob>();

    // Worker self-advertisement. WorkerName IS the routing key — it MUST match (Ordinal) the
    // server batch step's OnService("shipping"); a mismatch is SILENT (message waits forever in the
    // quorum queue) and the dashboard Workers panel won't reveal it.
    b.UseWorkerMode(w =>
    {
        w.WorkerName = cfg["UKBatch:Worker:WorkerName"] ?? "shipping";
        w.ServerUrl = cfg["UKBatch:Worker:ServerUrl"] ?? "http://localhost:5070";
        w.Tags = ["fulfilment"];
    });
});

// RabbitMQ transport — receiver mode. The consumer pump (IHostedService) connects at host start,
// declares + consumes the durable quorum queue `ukbatch.service.shipping`. Binds
// UKBatch:Transport:RabbitMQ from appsettings; HostName is overridden by env in docker-compose.
builder.Services.AddUKBatchRabbitMqTransport();

var app = builder.Build();

// No transport receiver endpoint to map — RabbitMQ delivery is consumer-pump driven, not HTTP. The
// minimal web host exists only so the worker stays alive + exposes a health probe (compose healthcheck).
app.MapHealthChecks("/healthz");
app.MapGet("/", () => "Sample.WorkerMode.Shipping — consuming ukbatch.service.shipping from the broker.");

app.Run();

namespace Sample.WorkerMode.Shipping
{
    public partial class Program;
}
