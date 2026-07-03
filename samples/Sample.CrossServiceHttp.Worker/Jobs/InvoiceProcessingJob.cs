using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceHttp.Worker.Jobs;

/// <summary>
/// Cross-service job invoked by the orchestrator's batch step 2 over HTTP transport. Reads the forwarded
/// <c>orderId</c> (produced by the local PrepareOrder step and carried across the boundary), "processes" it,
/// and produces an <c>invoiceId</c> that is returned to the orchestrator for the final local step to read —
/// a full local → cross-service → local data round-trip.
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

        // The forwarded orderId arrives as JSON (a JsonElement) across the boundary; the JSON-aware reader
        // resolves it. It is now the real upstream value — not the -1 placeholder the pre-forwarding sample saw.
        var orderId = context.Parameters.GetRequired<int>("orderId");
        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): processing orderId={OrderId} from source={Source}.",
            orderId, context.TriggeredBy);

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync can demonstrate
        // synchronous wait-for-result semantics.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        // Produce an invoice id and return it to the orchestrator on the reply — the final local step reads it.
        var invoiceId = $"INV-{orderId}";
        context.Outputs.Set("invoiceId", invoiceId);
        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): completed orderId={OrderId} — produced invoiceId={InvoiceId}, returning it to the orchestrator.",
            orderId, invoiceId);
    }
}
