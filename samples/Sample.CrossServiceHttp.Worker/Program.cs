using UKBatch.AspNetCore;
using UKBatch.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.UseInMemoryStorage();
    b.Configure(o =>
    {
        o.MaxDegreeOfParallelism = 4;
        o.ThisServiceName = "billing-worker";
    });
    b.AddJob<Sample.CrossServiceHttp.Worker.Jobs.InvoiceProcessingJob>();
});

// HTTP transport — receiver mode. Same shared secret as orchestrator (read from config).
builder.Services.AddUKBatchHttpTransport();

var app = builder.Build();

// Receiver endpoints mounted at /ukbatch/internal (fixed prefix per A11).
app.MapUKBatchHttpTransport();
app.MapHealthChecks("/healthz");
app.MapGet("/", () => "Sample.CrossServiceHttp.Worker — listening for /ukbatch/internal/jobs/* requests.");

app.Run();

namespace Sample.CrossServiceHttp.Worker
{
    public partial class Program;
}
