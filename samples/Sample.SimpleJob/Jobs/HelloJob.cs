using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.SimpleJob.Jobs;

/// <summary>
/// Trivial demo job that logs who triggered the execution. Demonstrates the
/// <c>RestoreRequestActivity()</c> opt-in pattern.
/// </summary>
public sealed class HelloJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // REQUIRED for trace correlation — see UKBatch.AspNetCore README.
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "HelloJob executed (executionId={ExecutionId}, triggeredBy={TriggeredBy}).",
            context.ExecutionId,
            context.TriggeredBy ?? "<none>");
        return Task.CompletedTask;
    }
}
