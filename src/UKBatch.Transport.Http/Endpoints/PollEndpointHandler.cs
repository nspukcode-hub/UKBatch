using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Transport.Http.Receiver;

namespace UKBatch.Transport.Http.Endpoints;

/// <summary>
/// <c>GET /ukbatch/internal/jobs/poll?topic={topic}&amp;waitMs={ms}</c> handler.
/// Long-poll subscribe — drains queued messages for the requested topic, blocking up to
/// <see cref="HttpTransportOptions.LongPollMaxWait"/> for the first message.
/// </summary>
internal static class PollEndpointHandler
{
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        HttpTransportReceiver receiver,
        IOptions<HttpTransportOptions> options,
        ILogger<HttpTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(options);

        if (!context.Request.Query.TryGetValue("topic", out var topicValues) || topicValues.Count == 0)
        {
            return Results.Problem(
                type: "ukbatch:validation-failed",
                title: "Missing required query parameter 'topic'.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var topic = topicValues[0]!;
        if (string.IsNullOrWhiteSpace(topic))
        {
            return Results.Problem(
                type: "ukbatch:validation-failed",
                title: "Query parameter 'topic' must be non-empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Honor client-supplied waitMs, clamped to [0, LongPollMaxWait]. The wire protocol
        // advertises waitMs={ms}; ignoring it server-side would be operator-hostile because
        // clients tuning for low-latency would still see 30s holds. The receiver caps via
        // min(clientWaitMs, LongPollMaxWait) so adversarial clients cannot extend holds beyond server policy.
        TimeSpan? clientWait = null;
        if (context.Request.Query.TryGetValue("waitMs", out var waitMsValues)
            && long.TryParse(waitMsValues.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var waitMs)
            && waitMs > 0)
        {
            clientWait = TimeSpan.FromMilliseconds(waitMs);
        }

        try
        {
            var messages = await receiver.AwaitMessagesAsync(topic, clientWait, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { messages });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("PollEndpoint: client disconnected (topic={Topic}).", topic);
            return Results.Ok(new { messages = Array.Empty<object>() });
        }
    }
}
