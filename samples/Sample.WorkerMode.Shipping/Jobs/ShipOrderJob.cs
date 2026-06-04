using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Shipping.Jobs;

/// <summary>
/// Cross-service job invoked by the server's batch step 2 over the RabbitMQ broker. "Ships an order"
/// (no-op log + short sleep) and returns a Completed status to the orchestrator via the request/reply
/// (direct-reply-to) path.
/// </summary>
/// <remarks>
/// STATELESS on purpose — Step Output Forwarding (cross-step parameter propagation) is a v0.2 concern,
/// so this job reads NO inbound parameters and produces NO output. The batch step's
/// <c>OnService("shipping")</c> routes to this worker by service name; the job NAME is
/// <c>"ShipOrder"</c> (the <see cref="JobAttribute.Name"/> below), which the server's batch definition
/// references as the step's <c>JobName</c>.
/// </remarks>
[Job(Name = "ShipOrder")]
public sealed class ShipOrderJob : IJob
{
    private readonly ILogger<ShipOrderJob> _logger;

    public ShipOrderJob(ILogger<ShipOrderJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger.LogInformation(
            "ShipOrderJob (shipping worker): received cross-service invocation from source={Source} over RabbitMQ.",
            context.TriggeredBy);

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync demonstrates
        // synchronous wait-for-result semantics over the broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "ShipOrderJob (shipping worker): completed — returning Completed status to the server via direct-reply-to.");
    }
}
