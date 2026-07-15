using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>
/// Express-shipping branch of the shipping decision: runs when the order amount is above the threshold.
/// Reads the routing value (<c>amount</c>) from its parameters so the log shows what it was selected for.
/// </summary>
public sealed class ShipExpressJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        var amount = context.Parameters.GetOrDefault<decimal>("amount", 0m);
        context.Logger.LogInformation(
            "ShipExpressJob ran for amount={Amount} (executionId={ExecutionId}).", amount, context.ExecutionId);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}
