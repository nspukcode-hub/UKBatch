using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceRabbitMQ.Orchestrator.Jobs;

/// <summary>
/// Step 1 of the cross-service RabbitMQ demo batch. Runs locally on the orchestrator process. Generates a
/// synthetic order id and forwards it via <see cref="JobContext.Outputs"/> — the next step (InvoiceProcessing,
/// on the billing worker across the broker) receives it as a parameter and processes the real value.
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
        context.Outputs.Set("orderId", orderId);
        _logger.LogInformation(
            "PrepareOrderJob (orchestrator side): generated orderId={OrderId} and forwarded it. Next step crosses the RabbitMQ broker to billing-worker.",
            orderId);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
    }
}
