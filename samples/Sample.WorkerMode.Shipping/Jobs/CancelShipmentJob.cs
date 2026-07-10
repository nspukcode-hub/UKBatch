using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Shipping.Jobs;

/// <summary>
/// Compensator for <see cref="ShipOrderJob"/>: recalls the shipment a completed ShipOrder step created,
/// when a LATER batch step fails and the saga unwind runs. Reads the forwarded <c>invoiceId</c> so the
/// recall names the same business document the shipment was created for.
/// </summary>
/// <remarks>
/// Compensators should be idempotent (a narrow crash window can re-run the last one); recalling an
/// already-recalled shipment is a no-op here. The optional <c>compensationDelaySeconds</c> parameter
/// (from the run's initial parameters) stretches the work so a host restart can be demonstrated
/// mid-compensation.
/// </remarks>
[Job(Name = "CancelShipment")]
public sealed class CancelShipmentJob : IJob
{
    private readonly ILogger<CancelShipmentJob> _logger;

    public CancelShipmentJob(ILogger<CancelShipmentJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var invoiceId = context.Parameters.GetOrDefault<string>("invoiceId", "(unknown)");
        _logger.LogWarning(
            "CancelShipmentJob (shipping worker): COMPENSATING — recalling shipment for invoice {InvoiceId} because a later batch step failed.",
            invoiceId);

        var delaySeconds = context.Parameters.GetOrDefault("compensationDelaySeconds", 0);
        if (delaySeconds > 0)
        {
            _logger.LogInformation("CancelShipmentJob: simulating {Delay}s of compensation work (restart-demo hook).", delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("CancelShipmentJob (shipping worker): shipment for invoice {InvoiceId} recalled.", invoiceId);
    }
}
