using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>
/// Standard-shipping branch of the shipping decision: runs when the order amount is at or below the
/// threshold (the else branch). Reads the routing value (<c>amount</c>) so the log shows what it was
/// selected for.
/// </summary>
public sealed class ShipStandardJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        var amount = context.Parameters.GetOrDefault<decimal>("amount", 0m);
        context.Logger.LogInformation(
            "ShipStandardJob ran for amount={Amount} (executionId={ExecutionId}).", amount, context.ExecutionId);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}
