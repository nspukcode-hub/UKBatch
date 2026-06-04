using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.Runtime;

namespace UKBatch.Transport.Http.Endpoints;

/// <summary>
/// <c>POST /ukbatch/internal/jobs/invoke</c> handler. Synchronous request/reply —
/// triggers the named job locally and awaits terminal status via
/// <see cref="IJobExecutionAwaiter"/>. Returns <see cref="JobResult"/>.
/// </summary>
internal static class InvokeEndpointHandler
{
    // JsonStringEnumConverter present so inbound JobMessage with string enums
    // (when sender is configured with ConfigureHttpJsonOptions / AddUKBatchApi) deserializes
    // correctly.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<IResult> HandleAsync(
        HttpContext context,
        IJobRunner runner,
        IJobExecutionAwaiter awaiter,
        IJobExecutionReader jobStore,
        MessageIdDedupeCache messageIdCache,
        IOptions<HttpTransportOptions> options,
        ILogger<HttpTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(awaiter);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(messageIdCache);
        ArgumentNullException.ThrowIfNull(options);

        // Filter buffered the body; rewind.
        context.Request.Body.Position = 0;
        JobMessage? message;
        try
        {
            message = await JsonSerializer.DeserializeAsync<JobMessage>(
                context.Request.Body, JsonOpts, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "InvokeEndpoint: malformed JobMessage JSON.");
            return Results.Problem(
                type: "ukbatch:validation-failed",
                title: "Malformed JobMessage envelope.",
                statusCode: StatusCodes.Status400BadRequest,
                detail: ex.Message);
        }

        if (message is null || string.IsNullOrEmpty(message.MessageId) || string.IsNullOrEmpty(message.JobName))
        {
            return Results.Problem(
                type: "ukbatch:validation-failed",
                title: "JobMessage missing required fields.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Idempotent replay — if MessageId was seen and result cached, replay it.
        if (!messageIdCache.TryAdd(message.MessageId))
        {
            if (messageIdCache.TryGetResult(message.MessageId, out var cached) && cached is not null)
            {
                logger.LogDebug("InvokeEndpoint: dedupe HIT — replaying cached JobResult for MessageId={MessageId}.", message.MessageId);
                return Results.Ok(cached);
            }
            // Seen but result not stored yet (race window) — accept and proceed.
        }

        // Compute the wall-clock timeout — sender's X-UKBatch-Timeout-Ms header or options.DefaultRequestTimeout.
        TimeSpan budget = options.Value.DefaultRequestTimeout;
        if (context.Request.Headers.TryGetValue("X-UKBatch-Timeout-Ms", out var hdrValues)
            && long.TryParse(hdrValues.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            && ms > 0)
        {
            budget = TimeSpan.FromMilliseconds(ms);
        }
        using var timeoutCts = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeoutCts.Token);

        JobExecution execution;
        try
        {
            var parameters = JobParameters.WrapWithoutCopy(
                new Dictionary<string, object?>(message.Parameters, StringComparer.Ordinal));
            execution = await runner.TriggerAsync(
                message.JobName,
                parameters,
                triggeredBy: $"http-transport:{message.SourceService}",
                linked.Token).ConfigureAwait(false);
        }
        catch (JobNotRegisteredException ex)
        {
            // Typed catch (no reflection-by-name). JobNotRegisteredException
            // is `public sealed : InvalidOperationException` in the UKBatch.Runtime namespace;
            // Transport.Http has a project reference to UKBatch.Core so we can catch the
            // concrete type directly. Mirrors UKBatch.Api/Jobs/JobsEndpoints.cs.
            logger.LogWarning(ex, "InvokeEndpoint: job '{JobName}' not registered.", message.JobName);
            return Results.Problem(
                type: "ukbatch:job-not-registered",
                title: "Job not registered on this worker.",
                statusCode: StatusCodes.Status404NotFound,
                detail: ex.Message);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            return Results.Problem(
                type: "ukbatch:job-timeout",
                title: "Job did not reach terminal state in the configured wall-clock budget.",
                statusCode: StatusCodes.Status408RequestTimeout);
        }

        JobExecution terminal;
        try
        {
            terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            return Results.Problem(
                type: "ukbatch:job-timeout",
                title: "Job did not reach terminal state in the configured wall-clock budget.",
                statusCode: StatusCodes.Status408RequestTimeout);
        }

        // Re-fetch authoritative row (CompletedAtUtc, LastError, etc.) — awaiter returned a snapshot
        // when the watch loop saw terminal; the store row carries the post-terminal fields.
        var latest = await jobStore.GetAsync(terminal.ExecutionId, context.RequestAborted).ConfigureAwait(false)
            ?? terminal;

        var result = new JobResult
        {
            ExecutionId = latest.ExecutionId,
            Status = latest.Status,
            ReturnValues = null, // v0.1 — job runtime does not surface return values; reserved for a future release.
            ErrorMessage = latest.LastError,
            Headers = null,
            CompletedAtUtc = latest.CompletedAtUtc ?? DateTimeOffset.UtcNow,
        };
        messageIdCache.StoreResult(message.MessageId, result);
        return Results.Ok(result);
    }
}
