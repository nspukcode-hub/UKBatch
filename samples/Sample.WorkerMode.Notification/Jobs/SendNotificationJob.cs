using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Notification.Jobs;

/// <summary>
/// Cross-service job invoked by the server over the RabbitMQ broker. Sends a notification for the order.
/// When it runs downstream of the invoice + ship steps (the sequential worker-mode-demo), it reads the
/// values those steps forwarded — the <c>invoiceId</c> (produced two hops upstream by GenerateInvoice) and
/// the <c>trackingNumber</c> (produced one hop upstream by ShipOrder) — proof that step outputs flow across
/// the whole chain of services (invoicing → shipping → notification).
/// </summary>
/// <remarks>
/// The batch step's <c>OnService("notification")</c> routes to this worker by service name; the job NAME is
/// <c>"SendNotification"</c> (the <see cref="JobAttribute.Name"/> below). It reads its inputs DEFENSIVELY
/// (<c>TryGet</c>, not <c>GetRequired</c>) because the same job also runs where nothing forwarded these
/// values — as a parallel child alongside its producer, and as a compensation step after a failed invoice —
/// so it degrades gracefully rather than requiring them.
/// </remarks>
[Job(Name = "SendNotification")]
public sealed class SendNotificationJob : IJob
{
    private readonly ILogger<SendNotificationJob> _logger;

    public SendNotificationJob(ILogger<SendNotificationJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Read the values forwarded down the chain, if any. invoiceId came from GenerateInvoice (two hops
        // upstream); trackingNumber from ShipOrder (one hop upstream). Both crossed service boundaries as JSON.
        var hasInvoice = context.Parameters.TryGet<string>("invoiceId", out var invoiceId);
        var hasTracking = context.Parameters.TryGet<string>("trackingNumber", out var trackingNumber);

        if (hasInvoice || hasTracking)
        {
            _logger.LogInformation(
                "SendNotificationJob (notification worker): notifying customer — invoice {InvoiceId} shipped as tracking {TrackingNumber} (values forwarded across invoicing → shipping → notification).",
                hasInvoice ? invoiceId : "(none)", hasTracking ? trackingNumber : "(none)");
        }
        else
        {
            _logger.LogInformation(
                "SendNotificationJob (notification worker): received cross-service invocation from source={Source} — no forwarded order details (ran as a parallel child or a compensation step).",
                context.TriggeredBy);
        }

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync demonstrates
        // synchronous wait-for-result semantics over the broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "SendNotificationJob (notification worker): completed — returning Completed status to the server via direct-reply-to.");
    }
}
