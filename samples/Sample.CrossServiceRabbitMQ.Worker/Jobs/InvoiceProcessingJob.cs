using UKBatch.Abstractions.Jobs;

namespace Sample.CrossServiceRabbitMQ.Worker.Jobs;

/// <summary>
/// Cross-service job invoked by the orchestrator's batch step 2 over the RabbitMQ broker. "Processes"
/// the request (no-op log + sleep) and returns a Completed status to the orchestrator via the
/// request/reply (direct-reply-to) path. Stateless on purpose — Step Output Forwarding (parameter
/// propagation across steps) is a v0.2 concern, so no inbound parameters are read.
/// </summary>
[Job(Name = "InvoiceProcessing")]
public sealed class InvoiceProcessingJob : IJob
{
    private readonly ILogger<InvoiceProcessingJob> _logger;

    public InvoiceProcessingJob(ILogger<InvoiceProcessingJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): received cross-service invocation from source={Source} over RabbitMQ.",
            context.TriggeredBy);

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync demonstrates
        // synchronous wait-for-result semantics over the broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "InvoiceProcessingJob (worker side): completed — returning Completed status to orchestrator via direct-reply-to.");
    }
}
