using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Common;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Runtime;

namespace UKBatch.Api.Jobs;

/// <summary>Handlers for the <c>/jobs/*</c> surface.</summary>
internal static class JobsEndpoints
{
    /// <summary>Maps the Jobs endpoints onto the given route group.</summary>
    public static void Map(RouteGroupBuilder group) => Map(group, operationIdPrefix: null);

    /// <summary>
    /// Maps the Jobs endpoints with an optional OpenAPI operation-id prefix
    /// for dual-mount scenarios (e.g. <c>/api</c> + <c>/api/secured</c>).
    /// </summary>
    public static void Map(RouteGroupBuilder group, string? operationIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(group);
        var jobs = group.MapGroup("/jobs").WithTags("Jobs");

        jobs.MapGet("/", (
                IJobDefinitionLookup lookup,
                IOptions<UKBatchOptions> options,
                [FromQuery] int? offset,
                [FromQuery] int? limit,
                [FromQuery] bool? partitioned) =>
            {
                if (!PaginationDefaults.TryValidate(options, offset, limit, out var effectiveOffset, out var effectiveLimit, out var errors))
                {
                    return Results.ValidationProblem(errors);
                }
                IReadOnlyList<JobDefinition> snapshot = lookup.All();
                IEnumerable<JobDefinition> filtered = snapshot;
                if (partitioned is { } p)
                {
                    filtered = filtered.Where(d => d.IsPartitioned == p);
                }
                var materialized = filtered.ToList();
                var page = materialized
                    .Skip(effectiveOffset)
                    .Take(effectiveLimit)
                    .Select(JobDefinitionDto.FromModel)
                    .ToList();
                return Results.Ok(new PageEnvelope<JobDefinitionDto>
                {
                    Items = page,
                    TotalCount = materialized.Count,
                    Offset = effectiveOffset,
                    Limit = effectiveLimit,
                });
            })
            .WithUKBatchName(operationIdPrefix, "ListJobs")
            .WithSummary("Lists registered job definitions in registration order. Use `partitioned=true` to filter to IPartitionedJob<T> types.");

        jobs.MapGet("/{name}", (string name, IJobDefinitionLookup lookup) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(name);
                var def = lookup.TryGet(name);
                if (def is null)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.JobNotRegistered,
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Job not registered",
                        detail: $"Job '{name}' is not registered.");
                }
                return Results.Ok(JobDefinitionDto.FromModel(def));
            })
            .WithUKBatchName(operationIdPrefix, "GetJob")
            .WithSummary("Returns the definition by name. 404 if not registered.");

        jobs.MapPost("/{name}/trigger", async (
                string name,
                JobTriggerRequest? body,
                IJobRunner runner,
                IJobDefinitionLookup lookup,
                IOptions<UKBatchOptions> options,
                IJobTriggerContext idCtx,
                IJobTraceContext traceCtx,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(name);
                var parameters = body?.Parameters is { } p
                    ? new JobParameters(p)
                    : JobParameters.Empty;

                // Reject a trigger that omits a required declared parameter before any dispatch
                // side-effect. An unknown name is left to fall through to the runner's typed
                // JobNotRegisteredException -> 404 path, so this never turns a not-registered job into a
                // 400. A present-but-null value does NOT satisfy a required parameter: the job's
                // GetRequired<T> rejects null at runtime, so it must be rejected here too.
                if (options.Value.EnforceDeclaredParameters
                    && lookup.TryGet(name) is { DeclaredParameters.Count: > 0 } def)
                {
                    var missing = new List<object>();
                    foreach (var descriptor in def.DeclaredParameters)
                    {
                        if (!descriptor.Required)
                        {
                            continue;
                        }
                        var satisfied =
                            (parameters.Values.TryGetValue(descriptor.Name, out var triggerValue) && triggerValue is not null)
                            || (def.DefaultParameters.TryGetValue(descriptor.Name, out var defaultValue) && defaultValue is not null);
                        if (!satisfied)
                        {
                            missing.Add(new
                            {
                                Path = descriptor.Name,
                                Message = $"required parameter '{descriptor.Name}' was not provided",
                            });
                        }
                    }
                    if (missing.Count > 0)
                    {
                        return Results.Problem(
                            type: ProblemDetailsConventions.JobParameterValidation,
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Job cannot be triggered",
                            detail: $"{missing.Count} required parameter(s) missing for job '{name}'.",
                            extensions: new Dictionary<string, object?> { ["errors"] = missing.ToArray() });
                    }
                }

                try
                {
                    // The single-job trigger is DECOUPLED from the caller's CT to avoid orphaning
                    // Pending rows under dispatcher backpressure when a client disconnects between
                    // InsertAsync (Storage) and EnqueueAsync (Dispatcher). Mirrors the
                    // TriggerBatchAsync decoupling invariant on IJobRunner. Trade-off: response time
                    // grows under backpressure rather than failing fast — preferred for data integrity.
                    // Prefer the trace-propagating extension so the request Activity flows into the job.
                    var execution = body?.TriggeredBy is { Length: > 0 } tb
                        ? await runner.TriggerAsync(name, parameters, tb, CancellationToken.None).ConfigureAwait(false)
                        : await runner.TriggerWithRequestContextAsync(idCtx, traceCtx, name, parameters, CancellationToken.None).ConfigureAwait(false);
                    return Results.Accepted(
                        $"/executions/{execution.ExecutionId}",
                        new JobTriggerResponse { ExecutionId = execution.ExecutionId });
                }
                catch (JobNotRegisteredException ex)
                {
                    // Typed catch (avoids brittle ex.Message.Contains(...) matching).
                    return Results.Problem(
                        type: ProblemDetailsConventions.JobNotRegistered,
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Job not registered",
                        detail: ex.Message);
                }
            })
            .WithUKBatchName(operationIdPrefix, "TriggerJob")
            .WithSummary("Triggers a single job. Returns 202 with the execution id; track via GET /executions/{id} or subscribe to the SignalR hub. Trigger is decoupled from caller CT to avoid orphaning Pending rows under dispatcher backpressure + client disconnect race.");
    }
}
