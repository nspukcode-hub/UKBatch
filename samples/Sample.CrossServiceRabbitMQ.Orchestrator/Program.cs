using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;
using UKBatch.Transport.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.UseInMemoryStorage();
    b.Configure(o =>
    {
        o.MaxDegreeOfParallelism = 4;
        // Cross-service identity. The batch's step 2 targets "billing-worker"; the broker routes the
        // request by service name and the worker replies via direct-reply-to. ThisServiceName also
        // declares the orchestrator's own durable service queue (harmless here — orchestrator is the
        // sender) and is REQUIRED for cross-service steps (BatchExecutor fail-fast otherwise).
        o.ThisServiceName = "orchestrator";
    });
    b.AddJob<Sample.CrossServiceRabbitMQ.Orchestrator.Jobs.PrepareOrderJob>();
    b.AddJob<Sample.CrossServiceRabbitMQ.Orchestrator.Jobs.FinalizeOrderJob>();
    b.AddBatch("cross-service-demo", batch => batch
        .RunJob<Sample.CrossServiceRabbitMQ.Orchestrator.Jobs.PrepareOrderJob>()
        .ThenRunJob("InvoiceProcessing", step => step.OnService("billing-worker"))
        .ThenRunJob<Sample.CrossServiceRabbitMQ.Orchestrator.Jobs.FinalizeOrderJob>());
});

builder.Services.AddUKBatchApi();
builder.Services.AddUKBatchDashboard(opts =>
{
    if (!opts.Services.Any(s => string.Equals(s.Name, "self", StringComparison.Ordinal)))
    {
        opts.Services.Add(new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = new Uri("http://localhost:5060/api/"),
            DisplayName = "Local Orchestrator",
        });
    }
});

// RabbitMQ transport — replaces the in-process default ITransport. Binds UKBatch:Transport:RabbitMQ
// from appsettings; the programmatic overlay below pins the broker host. Registers the consumer pump
// as an IHostedService so the connection + topology come up at host start (the reply-router needs the
// connection for cross-service RequestReplyAsync).
builder.Services.AddUKBatchRabbitMqTransport(o =>
{
    o.HostName = "localhost";
});

builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseAntiforgery();

// Mount the REST API under `/api`. The dashboard is mounted at `/dashboard/...` by convention.
app.MapGroup("/api").MapUKBatchApi();
app.MapUKBatchDashboard();

// MapStaticAssets — .NET 9/10 Blazor Web App convention. Mounts `_framework/blazor.web.js`
// + static web assets manifest. UseStaticFiles alone does NOT serve Blazor framework files.
// Without this, /dashboard route renders but CSS/JS assets return 403/404. Mirrors
// Sample.CrossServiceHttp.Orchestrator.
app.MapStaticAssets();

app.MapHealthChecks("/healthz");

// NOTE: there is NO transport receiver endpoint to map — RabbitMQ delivery is consumer-pump driven
// (registered as an IHostedService by AddUKBatchRabbitMqTransport). Unlike the HTTP transport sample,
// the orchestrator does not expose `/ukbatch/internal/...` endpoints.

app.MapGet("/", () => "Sample.CrossServiceRabbitMQ.Orchestrator — see /dashboard. POST /api/batches/by-name/cross-service-demo/run to trigger.");

app.Run();

namespace Sample.CrossServiceRabbitMQ.Orchestrator
{
    public partial class Program;
}
