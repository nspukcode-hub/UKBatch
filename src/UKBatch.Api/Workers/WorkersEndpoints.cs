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
    /// <summary>Maximum length of the worker name and of each advertised job name. Caps the per-entry
    /// memory a single beat can pin, independent of the count caps below.</summary>
    private const int MaxNameLength = 200;

    /// <summary>Maximum length of each free-form tag string.</summary>
    private const int MaxTagLength = 100;

    /// <summary>Largest accepted Jobs array — a misbehaving worker must not balloon a single entry.</summary>
    private const int MaxJobsCount = 1000;

    /// <summary>Largest accepted Tags array.</summary>
    private const int MaxTagsCount = 100;

    /// <summary>Largest accepted JobDescriptors array (mirrors <see cref="MaxJobsCount"/>).</summary>
    private const int MaxJobDescriptorsCount = 1000;

    /// <summary>Maximum declared parameters carried by a single job descriptor.</summary>
    private const int MaxParametersPerDescriptor = 200;

    /// <summary>Maximum length of a declared parameter description.</summary>
    private const int MaxDescriptionLength = 500;

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

                if (body.Name.Length > MaxNameLength)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["Name"] = [$"Worker name length must be <= {MaxNameLength} (got {body.Name.Length})."],
                    });
                }

                // The wire payload's init-default of [] does NOT protect against an explicit JSON null
                // ({"jobs":null} deserializes the property to null), so read through a null-coalescing
                // accessor before touching .Count to avoid a 500.
                var jobs = body.Jobs ?? [];
                var tags = body.Tags ?? [];

                // Count caps — a misbehaving worker must not balloon the registry.
                if (jobs.Count > MaxJobsCount || tags.Count > MaxTagsCount)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["Jobs"] = [$"Jobs <= {MaxJobsCount}, Tags <= {MaxTagsCount}."],
                    });
                }

                // Per-item caps: reject blank/null entries and over-length strings so one beat can't pin
                // unbounded memory under the count cap. Validate before normalizing so the caller gets a
                // clear field error rather than silently dropped data.
                foreach (var job in jobs)
                {
                    if (string.IsNullOrWhiteSpace(job) || job.Length > MaxNameLength)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["Jobs"] = [$"Each job name must be non-empty and <= {MaxNameLength} chars."],
                        });
                    }
                }

                foreach (var tag in tags)
                {
                    if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["Tags"] = [$"Each tag must be non-empty and <= {MaxTagLength} chars."],
                        });
                    }
                }

                // {"jobDescriptors":null} deserializes the property to null; a null element or a null
                // Parameters list ({"parameters":null}) would also slip past the init-default. Guard all
                // three so a malformed beat returns a clear 400 rather than a 500.
                var jobDescriptors = body.JobDescriptors ?? [];
                if (jobDescriptors.Count > MaxJobDescriptorsCount)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["JobDescriptors"] = [$"JobDescriptors <= {MaxJobDescriptorsCount}."],
                    });
                }
                foreach (var descriptor in jobDescriptors)
                {
                    if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Name) || descriptor.Name.Length > MaxNameLength)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["JobDescriptors"] = [$"Each descriptor Name must be non-empty and <= {MaxNameLength} chars."],
                        });
                    }
                    var descriptorParams = descriptor.Parameters ?? [];
                    if (descriptorParams.Count > MaxParametersPerDescriptor)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["JobDescriptors"] = [$"Each descriptor carries <= {MaxParametersPerDescriptor} parameters."],
                        });
                    }
                    foreach (var declared in descriptorParams)
                    {
                        if (declared is null || string.IsNullOrWhiteSpace(declared.Name) || declared.Name.Length > MaxNameLength
                            || (declared.Description is { Length: var dl } && dl > MaxDescriptionLength))
                        {
                            return Results.ValidationProblem(new Dictionary<string, string[]>
                            {
                                ["JobDescriptors"] = [$"Each parameter Name must be non-empty and <= {MaxNameLength}; Description <= {MaxDescriptionLength}."],
                            });
                        }
                    }
                }

                // Normalize each descriptor's Parameters to a non-null list so the registry and the list
                // snapshot never see null (an explicit {"parameters":null} slips past the init-default).
                var normalizedDescriptors = jobDescriptors
                    .Select(d => d with { Parameters = d.Parameters ?? [] })
                    .ToArray();

                // Store a beat with non-null lists so the registry and the list snapshot never see null.
                var normalized = body with { Jobs = jobs, Tags = tags, JobDescriptors = normalizedDescriptors };
                registry.Upsert(normalized, clock.GetUtcNow());
                return Results.Accepted();
            })
            .WithUKBatchName(operationIdPrefix, "WorkerBeat")
            .WithUKBatchAccess(UKBatchAccessKind.Ingest)
            .WithSummary("Worker heartbeat ingest (observability only — NEVER consulted for dispatch). 202 on accept.");

        // GET /api/workers — live snapshot.
        workers.MapGet("/", (IWorkerRegistry registry, TimeProvider clock) =>
                Results.Ok(registry.List(clock.GetUtcNow())))
            .WithUKBatchName(operationIdPrefix, "ListWorkers")
            .WithUKBatchAccess(UKBatchAccessKind.Read)
            .WithSummary("Lists known workers (live, TTL'd). `Online=false` rows are recently-departed workers retained until the hard-evict horizon.");
    }
}
