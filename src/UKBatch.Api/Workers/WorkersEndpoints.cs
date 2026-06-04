using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using UKBatch.Abstractions.Workers;
using UKBatch.Api.Common;

namespace UKBatch.Api.Workers;

/// <summary>
/// Handlers for the <c>/workers/*</c> surface (worker observability). Mirrors the
/// <c>JobsEndpoints</c> pattern (<c>internal static</c>, <c>Map(group, operationIdPrefix)</c>,
/// <see cref="RouteHandlerBuilderExtensions.WithUKBatchName"/>). <b>Auth-agnostic</b> — no
/// <c>RequireAuthorization</c> inside; the caller chains it on the route group.
/// </summary>
internal static class WorkersEndpoints
{
    /// <summary>Maps the Workers endpoints onto the given route group.</summary>
    public static void Map(RouteGroupBuilder group) => Map(group, operationIdPrefix: null);

    /// <summary>
    /// Maps the Workers endpoints with an optional OpenAPI operation-id prefix for dual-mount
    /// scenarios (e.g. <c>/api</c> + <c>/api/secured</c>). The prefix path already works because
    /// <see cref="RouteHandlerBuilderExtensions.WithUKBatchName"/> turns <c>"WorkerBeat"</c> into
    /// <c>"SecuredWorkerBeat"</c> — no special casing.
    /// </summary>
    public static void Map(RouteGroupBuilder group, string? operationIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(group);
        var workers = group.MapGroup("/workers").WithTags("Workers");

        // POST /api/workers/beat — observability ingest. 202 Accepted.
        workers.MapPost("/beat", (WorkerBeatRequest? body, IWorkerRegistry registry, TimeProvider clock) =>
            {
                if (body is null || string.IsNullOrWhiteSpace(body.Name))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["Name"] = ["Worker beat requires a non-empty Name."],
                    });
                }

                // Defensive caps — a misbehaving worker must not balloon the registry.
                if (body.Jobs.Count > 1000 || body.Tags.Count > 100)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["Jobs"] = ["Jobs <= 1000, Tags <= 100."],
                    });
                }

                registry.Upsert(body, clock.GetUtcNow());
                return Results.Accepted();
            })
            .WithUKBatchName(operationIdPrefix, "WorkerBeat")
            .WithSummary("Worker heartbeat ingest (observability only — NEVER consulted for dispatch). 202 on accept.");

        // GET /api/workers — live snapshot.
        workers.MapGet("/", (IWorkerRegistry registry, TimeProvider clock) =>
                Results.Ok(registry.List(clock.GetUtcNow())))
            .WithUKBatchName(operationIdPrefix, "ListWorkers")
            .WithSummary("Lists known workers (live, TTL'd). `Online=false` rows are recently-departed workers retained until the hard-evict horizon.");
    }
}
