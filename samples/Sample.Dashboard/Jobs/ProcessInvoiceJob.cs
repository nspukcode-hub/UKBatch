using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>
/// Step 2 of the order pipeline — consumes the previous step's output and produces its own. It reads the
/// forwarded <c>orderId</c> (scalar) and <c>order</c> (object) from its parameters, then records an
/// <c>invoiceId</c> for the final step.
/// </summary>
public sealed class ProcessInvoiceJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();

        // Read the forwarded values. GetRequired throws if a value the contract promises is missing.
        var orderId = context.Parameters.GetRequired<int>("orderId");
        var order = context.Parameters.GetRequired<DemoOrder>("order");

        var invoiceId = $"INV-{orderId}";
        context.Outputs.Set("invoiceId", invoiceId);

        context.Logger.LogInformation(
            "ProcessInvoiceJob received forwarded orderId={OrderId}, order.Customer={Customer}, order.Total={Total}; produced invoiceId={InvoiceId}.",
            orderId, order.Customer, order.Total, invoiceId);

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}
