using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceHttp.Orchestrator.Jobs;

/// <summary>
/// Step 3 of the cross-service demo batch — runs locally on the orchestrator process AFTER the
/// cross-service InvoiceProcessing step returns success. In production this would archive results,
/// notify downstream, etc.
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
        _logger.LogInformation(
            "FinalizeOrderJob (orchestrator side): cross-service step completed; finalizing.");
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
    }
}
