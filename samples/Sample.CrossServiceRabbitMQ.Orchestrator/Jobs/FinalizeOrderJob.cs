using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceRabbitMQ.Orchestrator.Jobs;

/// <summary>
/// Step 3 of the cross-service RabbitMQ demo batch — runs locally on the orchestrator process AFTER
/// the cross-service InvoiceProcessing step returns success over the broker. In production this would
/// archive results, notify downstream, etc.
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
            "FinalizeOrderJob (orchestrator side): cross-service step completed over RabbitMQ; finalizing.");
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
    }
}
