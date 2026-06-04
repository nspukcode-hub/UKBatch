using UKBatch.AspNetCore;
using UKBatch.Transport.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.UseInMemoryStorage();
    b.Configure(o =>
    {
        o.MaxDegreeOfParallelism = 4;
        // Service identity — the broker routes "billing-worker"-targeted messages to this node's
        // durable quorum service queue, which the consumer pump declares + consumes at host start.
        o.ThisServiceName = "billing-worker";
    });
    b.AddJob<Sample.CrossServiceRabbitMQ.Worker.Jobs.InvoiceProcessingJob>();
});

// RabbitMQ transport — receiver mode. AddUKBatchRabbitMqTransport registers the consumer pump as an
// IHostedService; StartAsync connects, declares the topology and begins consuming
// `ukbatch.service.billing-worker`. Binds UKBatch:Transport:RabbitMQ from appsettings.
builder.Services.AddUKBatchRabbitMqTransport(o =>
{
    o.HostName = "localhost";
});

var app = builder.Build();

// No transport receiver endpoint to map — RabbitMQ delivery is consumer-pump driven, not HTTP. The
// minimal web host exists only so the worker stays alive + exposes a health probe.
app.MapHealthChecks("/healthz");
app.MapGet("/", () => "Sample.CrossServiceRabbitMQ.Worker — consuming ukbatch.service.billing-worker from the broker.");

app.Run();

namespace Sample.CrossServiceRabbitMQ.Worker
{
    public partial class Program;
}
