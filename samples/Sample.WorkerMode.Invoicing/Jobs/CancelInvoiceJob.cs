using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Invoicing.Jobs;

/// <summary>
/// Compensator for <see cref="GenerateInvoiceJob"/>: voids the invoice a completed GenerateInvoice step
/// produced, when a LATER batch step fails and the saga unwind runs. It reads the produced
/// <c>invoiceId</c> from its parameters — a compensator receives the run's initial parameters plus every
/// output accumulated up to the failure, so the id the original step forwarded is available here.
/// </summary>
/// <remarks>
/// Compensators should be idempotent: after a crash mid-unwind the runtime skips a compensator whose
/// execution provably completed, but the write that records completion has a narrow crash window, so a
/// re-run must be harmless (voiding an already-voided invoice is a no-op here). The optional
/// <c>compensationDelaySeconds</c> parameter (from the run's initial parameters) stretches the work so a
/// host restart can be demonstrated mid-compensation.
/// </remarks>
[Job(Name = "CancelInvoice")]
public sealed class CancelInvoiceJob : IJob
{
    private readonly ILogger<CancelInvoiceJob> _logger;

    public CancelInvoiceJob(ILogger<CancelInvoiceJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var invoiceId = context.Parameters.GetOrDefault<string>("invoiceId", "(unknown)");
        _logger.LogWarning(
            "CancelInvoiceJob (invoicing worker): COMPENSATING — voiding invoice {InvoiceId} because a later batch step failed.",
            invoiceId);

        var delaySeconds = context.Parameters.GetOrDefault("compensationDelaySeconds", 0);
        if (delaySeconds > 0)
        {
            _logger.LogInformation("CancelInvoiceJob: simulating {Delay}s of compensation work (restart-demo hook).", delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("CancelInvoiceJob (invoicing worker): invoice {InvoiceId} voided.", invoiceId);
    }
}
