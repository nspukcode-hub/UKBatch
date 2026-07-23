using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Api.Common;
using UKBatch.Runtime;

namespace UKBatch.Api.Executions;

/// <summary>Handlers for the <c>/executions/*</c> surface.</summary>
internal static class ExecutionsEndpoints
{
    /// <summary>Maps the Executions endpoints onto the given route group.</summary>
    public static void Map(RouteGroupBuilder group) => Map(group, operationIdPrefix: null);

    /// <summary>Maps the Executions endpoints with an optional operation-id prefix for dual-mount scenarios.</summary>
    public static void Map(RouteGroupBuilder group, string? operationIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(group);
        var executions = group.MapGroup("/executions").WithTags("Executions");

        executions.MapGet("/{id}", async (string id, IJobExecutionReader reader, CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                var exec = await reader.GetAsync(id, ct).ConfigureAwait(false);
                if (exec is null)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ExecutionNotFound,
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Execution not found",
                        detail: $"Execution '{id}' not found.");
                }
                return Results.Ok(exec);
            })
            .WithUKBatchName(operationIdPrefix, "GetExecution")
            .WithUKBatchAccess(UKBatchAccessKind.Read)
            .WithSummary("Returns a single execution snapshot.");

        executions.MapPost("/query", async (
                JobQueryRequest body,
                IJobExecutionReader reader,
                IOptions<UKBatchOptions> options,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);
                if (!PaginationDefaults.TryValidate(options, body.Offset, body.Limit, out var effectiveOffset, out var effectiveLimit, out var errors))
                {
                    return Results.ValidationProblem(errors);
                }
                // Input bounds on Statuses[] and SearchText to prevent pathological linear scans
                // in the in-memory reader. Adapter packages MAY tune higher via
                // UKBatchOptions.MaxQueryStatusesCount / MaxQuerySearchTextLength.
                var opts = options.Value;
                var inputErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (body.Statuses is { } statuses && statuses.Count > opts.MaxQueryStatusesCount)
                {
                    inputErrors["statuses"] = [$"statuses array length must be <= {opts.MaxQueryStatusesCount} (got {statuses.Count})."];
                }
                if (!string.IsNullOrEmpty(body.SearchText) && body.SearchText.Length > opts.MaxQuerySearchTextLength)
                {
                    inputErrors["searchText"] = [$"searchText length must be <= {opts.MaxQuerySearchTextLength} (got {body.SearchText.Length})."];
                }
                // Input validation on BatchDefinitionId. Empty string ≠ null;
                // empty rejected explicitly so REST callers get a clear error rather than silent skip.
                // 64-char cap gives headroom over UUIDv7 hex N-format (32) for caller-supplied store ids.
                if (body.BatchDefinitionId is { } batchDefId)
                {
                    if (batchDefId.Length == 0)
                    {
                        inputErrors["batchDefinitionId"] = ["batchDefinitionId must not be empty when provided."];
                    }
                    else if (batchDefId.Length > 64)
                    {
                        inputErrors["batchDefinitionId"] = [$"batchDefinitionId length must be <= 64 (got {batchDefId.Length})."];
                    }
                }
                if (inputErrors.Count > 0)
                {
                    return Results.ValidationProblem(inputErrors);
                }
                var query = body.ToQuery() with { Offset = effectiveOffset, Limit = effectiveLimit };
                var items = await reader.QueryAsync(query, ct).ConfigureAwait(false);
                // CountAsync applies the same filter but ignores Offset/Limit (reader contract), so the
                // envelope carries the filter-wide total — pagers need it to know more pages exist.
                var totalCount = await reader.CountAsync(query, ct).ConfigureAwait(false);
                return Results.Ok(new PageEnvelope<JobExecution>
                {
                    Items = items,
                    TotalCount = totalCount,
                    Offset = effectiveOffset,
                    Limit = effectiveLimit,
                });
            })
            .WithUKBatchName(operationIdPrefix, "QueryExecutions")
            .WithUKBatchAccess(UKBatchAccessKind.Read)
            .WithSummary("Paginated query. POST is used because JobQuery is rich (Statuses[], UTC bounds, search text). Subject to UKBatchOptions.MaxQueryStatusesCount + MaxQuerySearchTextLength caps.");

        executions.MapPost("/{id}/cancel", async (string id, IJobRunner runner, CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                try
                {
                    await runner.CancelAsync(id, ct).ConfigureAwait(false);
                    return Results.NoContent();
                }
                catch (JobExecutionNotFoundException ex)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ExecutionNotFound,
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Execution not found",
                        detail: ex.Message);
                }
            })
            .WithUKBatchName(operationIdPrefix, "CancelExecution")
            .WithUKBatchAccess(UKBatchAccessKind.Write)
            .WithSummary("Cancels a single execution. Idempotent — returns 204 even if the execution is already terminal.");
    }
}
