using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>
/// Step 3 of the order pipeline — consumes the invoice id produced two steps earlier, proving that
/// outputs accumulate forward across the whole run (not just to the immediately following step).
/// </summary>
public sealed class FinalizeOrderJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();

        var orderId = context.Parameters.GetRequired<int>("orderId");
        var invoiceId = context.Parameters.GetRequired<string>("invoiceId");

        context.Logger.LogInformation(
            "FinalizeOrderJob completing order orderId={OrderId} with invoiceId={InvoiceId} (both forwarded from earlier steps).",
            orderId, invoiceId);

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}
