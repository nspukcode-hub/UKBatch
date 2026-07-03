using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Shipping.Jobs;

/// <summary>
/// Cross-service job invoked by the server's batch step 2 over the RabbitMQ broker. Ships the order for the
/// invoice produced by the upstream <c>GenerateInvoice</c> step — it reads the forwarded <c>invoiceId</c>
/// (scalar) and <c>invoice</c> (object) — and in turn produces a <c>trackingNumber</c> output forwarded to
/// the next step (SendNotification), so the pipeline carries data across all three services.
/// </summary>
/// <remarks>
/// The batch step's <c>OnService("shipping")</c> routes to this worker by service name; the job NAME is
/// <c>"ShipOrder"</c> (the <see cref="JobAttribute.Name"/> below). <see cref="Invoice"/> matches the
/// invoicing worker's shape so the object deserializes from the JSON that crossed the boundary.
/// </remarks>
[Job(Name = "ShipOrder")]
public sealed class ShipOrderJob : IJob
{
    private readonly ILogger<ShipOrderJob> _logger;

    public ShipOrderJob(ILogger<ShipOrderJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Read the outputs the invoicing worker produced, forwarded here by the server. GetRequired throws
        // if absent — the honest signal that forwarding is wired end-to-end (the pre-forwarding sample saw
        // nothing here). The scalar arrives as a JSON string; the object as JSON, deserialized into Invoice.
        var invoiceId = context.Parameters.GetRequired<string>("invoiceId");
        var invoice = context.Parameters.GetRequired<Invoice>("invoice");

        _logger.LogInformation(
            "ShipOrderJob (shipping worker): received cross-service invocation from source={Source} — shipping invoiceId={InvoiceId} for {Customer} (amount {Amount:C}).",
            context.TriggeredBy, invoiceId, invoice.Customer, invoice.Amount);

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync demonstrates
        // synchronous wait-for-result semantics over the broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        // Produce this step's own output — a shipment tracking number — forwarded to the next step
        // (SendNotification), so a downstream service reads a value THIS step created, not just the invoice.
        var trackingNumber = $"TRK-{Random.Shared.Next(100000, 999999)}";
        context.Outputs.Set("trackingNumber", trackingNumber);

        _logger.LogInformation(
            "ShipOrderJob (shipping worker): completed shipment for invoiceId={InvoiceId} — produced trackingNumber={TrackingNumber}, returning it to the server via direct-reply-to.",
            invoiceId, trackingNumber);
    }
}
