using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>
/// Step 1 of the order pipeline — produces step output. It records an <c>orderId</c> (a scalar) and a
/// whole <c>order</c> object via <see cref="JobContext.Outputs"/>; both flow into the next step's
/// parameters automatically.
/// </summary>
public sealed class PrepareOrderJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();

        var order = new DemoOrder { Id = 5012, Customer = "Acme", Total = 1499.90m };

        // Record outputs for later steps. A scalar and an object both work.
        context.Outputs.Set("orderId", order.Id);
        context.Outputs.Set("order", order);

        context.Logger.LogInformation(
            "PrepareOrderJob produced output orderId={OrderId}, order={{ Id={Id}, Customer={Customer}, Total={Total} }} (forwarding to the next step).",
            order.Id, order.Id, order.Customer, order.Total);

        // Demo-only pacing so the live DAG shows a visible running→completed transition per node.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}
