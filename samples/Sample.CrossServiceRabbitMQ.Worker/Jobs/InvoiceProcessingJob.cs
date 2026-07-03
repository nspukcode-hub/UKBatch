using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceRabbitMQ.Worker.Jobs;

/// <summary>
/// Cross-service job invoked by the orchestrator's batch step 2 over the RabbitMQ broker. Reads the forwarded
/// <c>orderId</c> (produced by the local PrepareOrder step and carried across the broker), "processes" it,
/// and produces an <c>invoiceId</c> that rides the direct-reply-to reply back to the orchestrator for the
/// final local step to read — a full local → cross-service → local data round-trip over the broker.
/// </summary>
[Job(Name = "InvoiceProcessing")]
public sealed class InvoiceProcessingJob : IJob
{
    private readonly ILogger<InvoiceProcessingJob> _logger;

    public InvoiceProcessingJob(ILogger<InvoiceProcessingJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The forwarded orderId arrives as JSON (a JsonElement) across the broker; the JSON-aware reader
        // resolves it into the requested type.
        var orderId = context.Parameters.GetRequired<int>("orderId");
        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): processing orderId={OrderId} from source={Source} over RabbitMQ.",
            orderId, context.TriggeredBy);

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync demonstrates
        // synchronous wait-for-result semantics over the broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        // Produce an invoice id and return it to the orchestrator on the reply — the final local step reads it.
        var invoiceId = $"INV-{orderId}";
        context.Outputs.Set("invoiceId", invoiceId);
        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): completed orderId={OrderId} — produced invoiceId={InvoiceId}, returning it to the orchestrator via direct-reply-to.",
            orderId, invoiceId);
    }
}
