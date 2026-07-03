using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceHttp.Orchestrator.Jobs;

/// <summary>
/// Step 3 of the cross-service demo batch — runs locally on the orchestrator process AFTER the cross-service
/// InvoiceProcessing step returns success. Reads the <c>invoiceId</c> that the billing worker produced and
/// returned across the boundary (closing the local → cross-service → local round-trip). In production this
/// would archive results, notify downstream, etc.
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
        // step's output flowed back. It crossed the boundary as JSON; the JSON-aware reader resolves it.
        var invoiceId = context.Parameters.GetRequired<string>("invoiceId");
        _logger.LogInformation(
            "FinalizeOrderJob (orchestrator side): cross-service step completed and returned invoiceId={InvoiceId}; finalizing.",
            invoiceId);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
    }
}
