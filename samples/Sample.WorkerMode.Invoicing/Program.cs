using Sample.WorkerMode.Invoicing.Jobs;
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

    // The job's routing name is "GenerateInvoice" via [Job(Name = "GenerateInvoice")]; the server's
    // batch definition references it as the step JobName + OnService("invoicing").
    b.AddJob<GenerateInvoiceJob>();
    b.AddJob<CancelInvoiceJob>();

    // Intra-job parallelism demo: an IPartitionedJob<InvoiceRow> — "SELECT, then process
    // the rows on 3 concurrent workers". WithParallelism(3) IS the worker count (the runtime spins a
    // bounded channel + 3 consumer tasks); ContinueOnError = one bad row logs + counts as a failure
    // instead of killing the run. The heartbeat auto-advertises it, so it appears on the dashboard's
    // Jobs page and in the Create-batch job picker without further wiring.
    b.AddPartitionedJob<ReconcileInvoicesJob, ReconcileInvoicesJob.InvoiceRow>()
        .WithParallelism(3)
        .WithItemErrorPolicy(UKBatch.Abstractions.Jobs.ItemErrorPolicy.ContinueOnError);

    // Worker self-advertisement. WorkerName IS the routing key — it MUST match (Ordinal) the
    // server batch step's OnService("invoicing"); a mismatch is SILENT (message waits forever in the
    // quorum queue) and the dashboard Workers panel won't reveal it.
    b.UseWorkerMode(w =>
    {
        w.WorkerName = cfg["UKBatch:Worker:WorkerName"] ?? "invoicing";
        w.ServerUrl = cfg["UKBatch:Worker:ServerUrl"] ?? "http://localhost:5070";
        w.Tags = ["billing"];
    });
});

// RabbitMQ transport — receiver mode. The consumer pump (IHostedService) connects at host start,
// declares + consumes the durable quorum queue `ukbatch.service.invoicing`. Binds
// UKBatch:Transport:RabbitMQ from appsettings; HostName is overridden by env in docker-compose.
builder.Services.AddUKBatchRabbitMqTransport();

var app = builder.Build();

// No transport receiver endpoint to map — RabbitMQ delivery is consumer-pump driven, not HTTP. The
// minimal web host exists only so the worker stays alive + exposes a health probe (compose healthcheck).
app.MapHealthChecks("/healthz");
app.MapGet("/", () => "Sample.WorkerMode.Invoicing — consuming ukbatch.service.invoicing from the broker.");

app.Run();

namespace Sample.WorkerMode.Invoicing
{
    public partial class Program;
}
