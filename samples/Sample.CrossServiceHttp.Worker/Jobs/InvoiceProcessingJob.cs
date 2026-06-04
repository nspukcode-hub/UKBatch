using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceHttp.Worker.Jobs;

/// <summary>
/// Cross-service job invoked by the orchestrator's batch step 2. Receives an order id and
/// "processes" it (no-op log + sleep). Demonstrates the request/reply path over HTTP transport.
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
        var orderId = context.Parameters.TryGet<int>("orderId", out var v) ? v : -1;
        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): processing orderId={OrderId} from source={Source}.",
            orderId, context.TriggeredBy);

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync can demonstrate
        // synchronous wait-for-result semantics.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): completed orderId={OrderId} — returning Completed status to orchestrator.",
            orderId);
    }
}
