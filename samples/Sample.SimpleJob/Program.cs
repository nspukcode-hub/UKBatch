using Sample.SimpleJob.DevAuth;
using Sample.SimpleJob.Jobs;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Storage;
using UKBatch.AspNetCore;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b =>
{
    b.Configure(o =>
    {
        o.MaxDegreeOfParallelism = 4;
        o.DispatcherChannelCapacity = 256;
    });
    b.AddJob<HelloJob>().Named(nameof(HelloJob));
    b.AddJob<ScheduledHeartbeatJob>().Named(nameof(ScheduledHeartbeatJob));
    b.AddPartitionedJob<ItemProcessorJob, int>().Named(nameof(ItemProcessorJob)).WithParallelism(4);
});

// DEVELOPMENT ONLY — header-based dev auth (X-Dev-User / X-Dev-Roles).
builder.Services
    .AddAuthentication(DevAuthSchemeOptions.SchemeName)
    .AddScheme<DevAuthSchemeOptions, DevAuthHandler>(DevAuthSchemeOptions.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz");

app.MapGet("/trigger/hello",
    async (IJobRunner runner, IJobTriggerContext idCtx, IJobTraceContext traceCtx, CancellationToken ct) =>
    {
        var execution = await runner.TriggerWithRequestContextAsync(
            idCtx, traceCtx, jobName: nameof(HelloJob), JobParameters.Empty, ct);
        return Results.Ok(new
        {
            execution.ExecutionId,
            execution.TriggeredBy,
            Status = execution.Status.ToString(),
        });
    });

app.MapGet("/trigger/items",
    async (IJobRunner runner, IJobTriggerContext idCtx, IJobTraceContext traceCtx, int count, CancellationToken ct) =>
    {
        var p = new JobParameters(new Dictionary<string, object?> { ["count"] = count });
        var execution = await runner.TriggerWithRequestContextAsync(
            idCtx, traceCtx, jobName: nameof(ItemProcessorJob), p, ct);
        return Results.Ok(new { execution.ExecutionId });
    });

app.MapGet("/trigger/scheduled",
    async (IJobRunner runner, IJobTriggerContext idCtx, IJobTraceContext traceCtx, CancellationToken ct) =>
    {
        var execution = await runner.TriggerWithRequestContextAsync(
            idCtx, traceCtx, jobName: nameof(ScheduledHeartbeatJob), JobParameters.Empty, ct);
        return Results.Ok(new { execution.ExecutionId });
    });

app.MapGet("/status/{executionId}",
    async (IJobExecutionReader reader, string executionId, CancellationToken ct) =>
    {
        var execution = await reader.GetAsync(executionId, ct);
        return execution is null ? Results.NotFound() : Results.Ok(execution);
    });

app.Run();

namespace Sample.SimpleJob
{
    /// <summary>Marker for WebApplicationFactory test discovery.</summary>
    public partial class Program;
}
