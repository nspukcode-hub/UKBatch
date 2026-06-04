using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;
using UKBatch.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.UseInMemoryStorage();
    b.Configure(o =>
    {
        o.MaxDegreeOfParallelism = 4;
        o.ThisServiceName = "orchestrator";
    });
    b.AddJob<Sample.CrossServiceHttp.Orchestrator.Jobs.PrepareOrderJob>();
    b.AddJob<Sample.CrossServiceHttp.Orchestrator.Jobs.FinalizeOrderJob>();
    b.AddBatch("cross-service-demo", batch => batch
        .RunJob<Sample.CrossServiceHttp.Orchestrator.Jobs.PrepareOrderJob>()
        .ThenRunJob("InvoiceProcessing", step => step.OnService("billing-worker"))
        .ThenRunJob<Sample.CrossServiceHttp.Orchestrator.Jobs.FinalizeOrderJob>());
});

builder.Services.AddUKBatchApi();
builder.Services.AddUKBatchDashboard(opts =>
{
    if (!opts.Services.Any(s => string.Equals(s.Name, "self", StringComparison.Ordinal)))
    {
        opts.Services.Add(new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = new Uri("http://localhost:5050/api/"),
            DisplayName = "Local Orchestrator",
        });
    }
});
builder.Services.AddUKBatchHttpTransport();

builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseAntiforgery();

// Mount the REST API under `/api`. The dashboard is mounted at `/dashboard/...` by convention.
app.MapGroup("/api").MapUKBatchApi();
app.MapUKBatchDashboard();

// MapStaticAssets — .NET 9/10 Blazor Web App convention. Mounts `_framework/blazor.web.js`
// + static web assets manifest. UseStaticFiles alone does NOT serve Blazor framework files.
// Without this, /dashboard route renders but CSS/JS assets return 403/404. Mirrors Sample.Dashboard.
app.MapStaticAssets();

app.MapHealthChecks("/healthz");

// NOTE: NO MapUKBatchHttpTransport() — orchestrator is sender-only.

app.MapGet("/", () => "Sample.CrossServiceHttp.Orchestrator — see /dashboard. POST /api/batches/by-name/cross-service-demo/run to trigger.");

app.Run();

namespace Sample.CrossServiceHttp.Orchestrator
{
    public partial class Program;
}
