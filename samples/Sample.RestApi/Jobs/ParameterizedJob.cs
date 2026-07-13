using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.RestApi.Jobs;

/// <summary>
/// Demonstrates declared parameters: the job announces what it expects at registration
/// (<c>WithParameter&lt;T&gt;</c>), which drives the typed dashboard trigger form, the per-job
/// REST/OpenAPI schema, and (in worker mode) the heartbeat catalog. A required parameter with no
/// default is rejected at the single-job REST trigger when it is omitted.
/// </summary>
public sealed class ParameterizedJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();

        var orderId = context.Parameters.GetRequired<string>("orderId");
        var retries = context.Parameters.GetOrDefault("retries", 3);
        var dryRun = context.Parameters.GetOrDefault("dryRun", false);

        context.Logger.LogInformation(
            "ParameterizedJob ran (orderId={OrderId}, retries={Retries}, dryRun={DryRun}).",
            orderId, retries, dryRun);
        return Task.CompletedTask;
    }
}
