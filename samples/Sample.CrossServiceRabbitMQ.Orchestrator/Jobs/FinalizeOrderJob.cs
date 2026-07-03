using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceRabbitMQ.Orchestrator.Jobs;

/// <summary>
/// Step 3 of the cross-service RabbitMQ demo batch — runs locally on the orchestrator process AFTER the
/// cross-service InvoiceProcessing step returns success over the broker. Reads the <c>invoiceId</c> the
/// billing worker produced and returned on the reply (closing the local → cross-service → local round-trip).
/// In production this would archive results, notify downstream, etc.
/// </summary>
public sealed class FinalizeOrderJob : IJob
{
    private readonly ILogger<FinalizeOrderJob> _logger;

    public FinalizeOrderJob(ILogger<FinalizeOrderJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The invoiceId was produced on the worker and returned to the orchestrator — proof the cross-service
        // step's output flowed back over the broker. It crossed as JSON; the JSON-aware reader resolves it.
        var invoiceId = context.Parameters.GetRequired<string>("invoiceId");
        _logger.LogInformation(
            "FinalizeOrderJob (orchestrator side): cross-service step completed over RabbitMQ and returned invoiceId={InvoiceId}; finalizing.",
            invoiceId);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
    }
}
