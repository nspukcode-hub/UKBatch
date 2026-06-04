using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceRabbitMQ.Orchestrator.Jobs;

/// <summary>
/// Step 1 of the cross-service RabbitMQ demo batch. Runs locally on the orchestrator process.
/// Generates a synthetic order id (logged only — not forwarded; Step Output Forwarding is a v0.2
/// concern) — in production this would typically validate / enrich the request payload.
/// </summary>
public sealed class PrepareOrderJob : IJob
{
    private readonly ILogger<PrepareOrderJob> _logger;

    public PrepareOrderJob(ILogger<PrepareOrderJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var orderId = Random.Shared.Next(1000, 9999);
        _logger.LogInformation(
            "PrepareOrderJob (orchestrator side): generated orderId={OrderId}. Next step crosses the RabbitMQ broker to billing-worker.",
            orderId);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
    }
}
