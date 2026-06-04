using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Receiver;

namespace UKBatch.Transport.Http.Endpoints;

/// <summary>
/// <c>POST /ukbatch/internal/jobs/publish</c> handler. Receives an
/// HMAC-verified <see cref="JobMessage"/> envelope and enqueues it into the per-topic receiver
/// channel. Returns 202 Accepted unconditionally on success.
/// </summary>
internal static class PublishEndpointHandler
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
        HttpTransportReceiver receiver,
        MessageIdDedupeCache messageIdCache,
        ILogger<HttpTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(messageIdCache);

        // Filter already buffered the body for HMAC verify; rewind and re-read.
        context.Request.Body.Position = 0;
        JobMessage? message;
        try
        {
            message = await JsonSerializer.DeserializeAsync<JobMessage>(
                context.Request.Body, JsonOpts, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "PublishEndpoint: malformed JobMessage JSON.");
            return Results.Problem(
                type: "ukbatch:validation-failed",
                title: "Malformed JobMessage envelope.",
                statusCode: StatusCodes.Status400BadRequest,
                detail: ex.Message);
        }

        if (message is null || string.IsNullOrEmpty(message.MessageId) || string.IsNullOrEmpty(message.JobName))
        {
            logger.LogWarning("PublishEndpoint: JobMessage missing required fields.");
            return Results.Problem(
                type: "ukbatch:validation-failed",
                title: "JobMessage missing required fields.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!messageIdCache.TryAdd(message.MessageId))
        {
            // Idempotent replay — already accepted.
            logger.LogDebug("PublishEndpoint: dedupe HIT for MessageId={MessageId} (idempotent replay).", message.MessageId);
            return Results.Accepted();
        }

        receiver.Enqueue(message.JobName, message);
        return Results.Accepted();
    }
}
