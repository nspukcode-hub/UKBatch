#if !NET10_0_OR_GREATER
using System.Security.Cryptography;
#endif
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Api.Common;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Runtime;
using UKBatch.Validation;

namespace UKBatch.Api.Batches;

/// <summary>Handlers for the <c>/batches/*</c> surface.</summary>
internal static class BatchesEndpoints
{
    /// <summary>Maps the Batches endpoints onto the given route group.</summary>
    public static void Map(RouteGroupBuilder group) => Map(group, operationIdPrefix: null);

    /// <summary>Maps the Batches endpoints with an optional operation-id prefix for dual-mount scenarios.</summary>
    public static void Map(RouteGroupBuilder group, string? operationIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(group);
        var batches = group.MapGroup("/batches").WithTags("Batches");

        // GET /batches — list across sources via IBatchCatalogService.
        batches.MapGet("/", async (
                IBatchCatalogService catalog,
                IOptions<UKBatchOptions> options,
                [FromQuery] BatchSource? source,
                [FromQuery] string? nameContains,
                [FromQuery] int? offset,
                [FromQuery] int? limit,
                CancellationToken ct) =>
            {
                if (!PaginationDefaults.TryValidate(options, offset, limit, out var effectiveOffset, out var effectiveLimit, out var errors))
                {
                    return Results.ValidationProblem(errors);
                }
                var page = await catalog.ListAsync(new BatchCatalogQuery
                {
                    Source = source,
                    NameContains = nameContains,
                    Offset = effectiveOffset,
                    Limit = effectiveLimit,
                }, ct).ConfigureAwait(false);
                return Results.Ok(new PageEnvelope<BatchDefinitionDto>
                {
                    Items = page.Items.Select(BatchDefinitionDto.FromModel).ToList(),
                    TotalCount = page.TotalCount,
                    Offset = page.Offset,
                    Limit = page.Limit,
                });
            })
            .WithUKBatchName(operationIdPrefix, "ListBatches")
            .WithSummary("Lists batch definitions across all sources via IBatchCatalogService. Code-source wins on name collision. Ordered by Name ascending.");

        // GET /batches/by-id/{id}
        batches.MapGet("/by-id/{id}", async (string id, IBatchCatalogService catalog, CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                var def = await catalog.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (def is null)
                {
                    return BatchNotFound($"Batch definition '{id}' not found.");
                }
                return Results.Ok(BatchDefinitionDto.FromModel(def));
            })
            .WithUKBatchName(operationIdPrefix, "GetBatchById")
            .WithSummary("Returns the batch definition by id from Code or Store sources. 404 if not found anywhere.");

        // GET /batches/by-name/{name}
        batches.MapGet("/by-name/{name}", async (
                string name,
                IBatchCatalogService catalog,
                [FromQuery] BatchSource? source,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(name);
                var def = await catalog.GetByNameAsync(name, source, ct).ConfigureAwait(false);
                if (def is null)
                {
                    return BatchNotFound($"Batch definition with name '{name}' not found.");
                }
                return Results.Ok(BatchDefinitionDto.FromModel(def));
            })
            .WithUKBatchName(operationIdPrefix, "GetBatchByName")
            .WithSummary("Returns the batch definition by name. If `source` is omitted, Code-source wins on collision.");

        // POST /batches/by-id/{id}/run
        batches.MapPost("/by-id/{id}/run", async (
                string id,
                BatchRunRequest? body,
                IBatchCatalogService catalog,
                IJobRunner runner,
                IJobTriggerContext idCtx,
                IJobTraceContext traceCtx,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                var def = await catalog.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (def is null)
                {
                    return BatchNotFound($"Batch definition '{id}' not found.");
                }
                var parameters = body?.InitialParameters is { } p ? new JobParameters(p) : null;
                return await TryRunBatchAsync(runner, idCtx, traceCtx, def, parameters, body, http).ConfigureAwait(false);
            })
            .WithUKBatchName(operationIdPrefix, "RunBatchById")
            .WithSummary("Triggers a new batch run by definition id. Returns 202 with the batch-run id.");

        // POST /batches/by-name/{name}/run
        batches.MapPost("/by-name/{name}/run", async (
                string name,
                BatchRunRequest? body,
                IBatchCatalogService catalog,
                IJobRunner runner,
                IJobTriggerContext idCtx,
                IJobTraceContext traceCtx,
                HttpContext http,
                [FromQuery] BatchSource? source,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(name);
                var def = await catalog.GetByNameAsync(name, source, ct).ConfigureAwait(false);
                if (def is null)
                {
                    return BatchNotFound($"Batch definition with name '{name}' not found.");
                }
                var parameters = body?.InitialParameters is { } p ? new JobParameters(p) : null;
                return await TryRunBatchAsync(runner, idCtx, traceCtx, def, parameters, body, http).ConfigureAwait(false);
            })
            .WithUKBatchName(operationIdPrefix, "RunBatchByName")
            .WithSummary("Triggers a new batch run by definition name. Returns 202 with the batch-run id.");

        // POST /batches — create (Dashboard or Api source only).
        batches.MapPost("/", async (
                CreateBatchRequest body,
                IBatchDefinitionStore store,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);
                if (body.Source == BatchSource.Code)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ValidationFailed,
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Code-source batches are immutable",
                        detail: "Code-source batches cannot be created via REST. Use Dashboard or Api.");
                }
                var def = new BatchDefinition
                {
                    // Use an inline UUIDv7 instead of the id helper in Core internals — this keeps
                    // the Api package's consumption of Core internals minimal.
                    Id = NewBatchId(),
                    Name = body.Name,
                    Source = body.Source,
                    Schedule = body.Schedule,
                    Steps = body.Steps,
                    FailurePolicy = body.FailurePolicy,
                    OnFailureSteps = body.OnFailureSteps,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    CreatedBy = body.CreatedBy,
                    Version = 0,
                    Metadata = body.Metadata,
                };
                var validation = BatchDefinitionValidator.Validate(def);
                if (!validation.IsValid)
                {
                    var errors = validation.Errors
                        .GroupBy(e => e.PropertyPath, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal);
                    return Results.ValidationProblem(errors);
                }
                try
                {
                    var created = await store.CreateAsync(def, ct).ConfigureAwait(false);
                    return Results.Created(
                        $"/batches/by-id/{created.Id}",
                        BatchDefinitionDto.FromModel(created));
                }
                catch (BatchDefinitionDuplicateNameException ex)
                {
                    // Typed catch — distinct ProblemDetails URI for name collision
                    // (vs. concurrency conflict on UpdateAsync). Dashboard create/edit forms render
                    // accurate error messages instead of a generic "Concurrency conflict".
                    return Results.Problem(
                        type: ProblemDetailsConventions.BatchDefinitionDuplicateName,
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Batch definition name already exists",
                        detail: ex.Message);
                }
            })
            .WithUKBatchName(operationIdPrefix, "CreateBatch")
            .WithSummary("Creates a Dashboard- or Api-source batch.");

        // PUT /batches/by-id/{id}
        batches.MapPut("/by-id/{id}", async (
                string id,
                UpdateBatchRequest body,
                IBatchDefinitionStore store,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                ArgumentNullException.ThrowIfNull(body);
                if (body.Source == BatchSource.Code)
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ValidationFailed,
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Code-source batches are immutable",
                        detail: "Code-source batches cannot be updated via REST.");
                }
                if (!string.Equals(id, body.Id, StringComparison.Ordinal))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["id"] = ["Route id must match body id."],
                    });
                }
                var def = new BatchDefinition
                {
                    Id = body.Id,
                    Name = body.Name,
                    Source = body.Source,
                    Schedule = body.Schedule,
                    Steps = body.Steps,
                    FailurePolicy = body.FailurePolicy,
                    OnFailureSteps = body.OnFailureSteps,
                    CreatedAtUtc = DateTimeOffset.UtcNow, // ignored by impl (versioned upsert preserves the original)
                    Version = body.Version,
                    Metadata = body.Metadata,
                };
                var validation = BatchDefinitionValidator.Validate(def);
                if (!validation.IsValid)
                {
                    var errors = validation.Errors
                        .GroupBy(e => e.PropertyPath, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal);
                    return Results.ValidationProblem(errors);
                }
                try
                {
                    var updated = await store.UpdateAsync(def, ct).ConfigureAwait(false);
                    return Results.Ok(BatchDefinitionDto.FromModel(updated));
                }
                catch (BatchDefinitionNotFoundException ex)
                {
                    // Typed 404 with the precise batch-definition-not-found URI
                    // (vs. generic batch-not-found which covers batch RUN id misses).
                    return Results.Problem(
                        type: ProblemDetailsConventions.BatchDefinitionNotFound,
                        statusCode: StatusCodes.Status404NotFound,
                        title: "Batch definition not found",
                        detail: ex.Message);
                }
                catch (BatchDefinitionDuplicateNameException ex)
                {
                    // Rename-to-existing-name path; distinct from concurrency conflict.
                    return Results.Problem(
                        type: ProblemDetailsConventions.BatchDefinitionDuplicateName,
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Batch definition name already exists",
                        detail: ex.Message);
                }
                catch (BatchConcurrencyConflictException ex)
                {
                    // Optimistic concurrency mismatch; distinct from duplicate name.
                    return Results.Problem(
                        type: ProblemDetailsConventions.ConcurrencyConflict,
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Concurrency conflict",
                        detail: ex.Message);
                }
            })
            .WithUKBatchName(operationIdPrefix, "UpdateBatch")
            .WithSummary("Updates a Store-source batch with optimistic concurrency.");

        // DELETE /batches/by-id/{id}
        batches.MapDelete("/by-id/{id}", async (
                string id,
                IBatchCatalogService catalog,
                IBatchDefinitionStore store,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(id);
                // Check if the resolved definition is Code-source — if so, reject with 400.
                var def = await catalog.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (def is { Source: BatchSource.Code })
                {
                    return Results.Problem(
                        type: ProblemDetailsConventions.ValidationFailed,
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Code-source batches are immutable",
                        detail: "Code-source batches cannot be deleted.");
                }
                await store.DeleteAsync(id, ct).ConfigureAwait(false);
                return Results.NoContent();
            })
            .WithUKBatchName(operationIdPrefix, "DeleteBatch")
            .WithSummary("Deletes a Store-source batch. Idempotent. Code-source batches return 400.");

        // GET /batches/{batchRunId}/status — RUN-keyed.
        batches.MapGet("/{batchRunId}/status", async (
                string batchRunId,
                IJobExecutionReader reader,
                IOptions<UKBatchOptions> options,
                [FromQuery] int? offset,
                [FromQuery] int? limit,
                CancellationToken ct) =>
            {
                ArgumentException.ThrowIfNullOrEmpty(batchRunId);
                if (!PaginationDefaults.TryValidate(options, offset, limit, out var effectiveOffset, out var effectiveLimit, out var errors))
                {
                    return Results.ValidationProblem(errors);
                }
                var query = new JobQuery
                {
                    BatchId = batchRunId,
                    Offset = effectiveOffset,
                    Limit = effectiveLimit,
                };
                var executions = await reader.QueryAsync(query, ct).ConfigureAwait(false);
                // CountAsync applies the same filter but ignores Offset/Limit (reader contract), so the
                // envelope carries the run-wide total — pagers need it to know more pages exist.
                var totalCount = await reader.CountAsync(query, ct).ConfigureAwait(false);
                return Results.Ok(new PageEnvelope<JobExecution>
                {
                    Items = executions,
                    TotalCount = totalCount,
                    Offset = effectiveOffset,
                    Limit = effectiveLimit,
                });
            })
            .WithUKBatchName(operationIdPrefix, "GetBatchRunStatus")
            .WithSummary("Returns the executions for ONE batch RUN (the id returned from /run). Empty list if no executions — NOT a 404.");
    }

    private static IResult BatchNotFound(string detail) =>
        Results.Problem(
            type: ProblemDetailsConventions.BatchNotFound,
            statusCode: StatusCodes.Status404NotFound,
            title: "Batch not found",
            detail: detail);

    /// <summary>
    /// Race-window-safe wrapper around <c>IJobRunner.TriggerBatchAsync</c>. The endpoint
    /// pre-resolves the definition via <c>catalog.GetByIdAsync</c> / <c>GetByNameAsync</c>, but a
    /// concurrent DELETE between resolution and trigger can cause the runtime to throw
    /// <see cref="BatchDefinitionNotFoundException"/>. The helper maps that to 404 with the
    /// precise <c>ukbatch:batch-definition-not-found</c> URI. Also handles the (rarer) race where
    /// a job is removed from the registry between AddBatch validation and dispatch.
    /// </summary>
    private static async Task<IResult> TryRunBatchAsync(
        IJobRunner runner,
        IJobTriggerContext idCtx,
        IJobTraceContext traceCtx,
        BatchDefinition def,
        JobParameters? parameters,
        BatchRunRequest? body,
        HttpContext http)
    {
        try
        {
            var batchRunId = body?.TriggeredBy is { Length: > 0 } tb
                ? await runner.TriggerBatchAsync(def.Id, parameters, tb, http.RequestAborted).ConfigureAwait(false)
                : await runner.TriggerBatchWithRequestContextAsync(idCtx, traceCtx, def.Id, parameters, http.RequestAborted).ConfigureAwait(false);
            return Results.Accepted(
                $"/batches/{batchRunId}/status",
                new BatchRunResponse { BatchId = batchRunId });
        }
        catch (BatchDefinitionNotFoundException ex)
        {
            // Race window — definition deleted between catalog.GetByIdAsync and TriggerBatchAsync.
            return Results.Problem(
                type: ProblemDetailsConventions.BatchDefinitionNotFound,
                statusCode: StatusCodes.Status404NotFound,
                title: "Batch definition not found",
                detail: ex.Message);
        }
        catch (JobNotRegisteredException ex)
        {
            // Race window — a JobRegistry.TryRemove between AddBatch validation and dispatch
            // is theoretically possible. Map to the same 404 typed URI as the standalone trigger path.
            return Results.Problem(
                type: ProblemDetailsConventions.JobNotRegistered,
                statusCode: StatusCodes.Status404NotFound,
                title: "Job not registered",
                detail: ex.Message);
        }
    }

    private static string NewBatchId()
    {
#if NET10_0_OR_GREATER
        return Guid.CreateVersion7().ToString("N");
#else
        // UUIDv7 (RFC 9562) for net8.0, where Guid.CreateVersion7 is unavailable: 48-bit
        // big-endian Unix-ms timestamp + version 7 + variant 10 + 74 random bits. The
        // big-endian Guid ctor reproduces CreateVersion7's byte layout exactly, so ids
        // generated on net8.0 and net10.0 sort and round-trip identically.
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var unixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 10xx
        return new Guid(bytes, bigEndian: true).ToString("N");
#endif
    }
}
