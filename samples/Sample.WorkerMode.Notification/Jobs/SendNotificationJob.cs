using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Notification.Jobs;

/// <summary>
/// Cross-service job invoked by the server's batch step 3 over the RabbitMQ broker. "Sends a
/// notification" (no-op log + short sleep) and returns a Completed status to the orchestrator via the
/// request/reply (direct-reply-to) path. Step 3 of the approval-parallel-demo batch — it runs only
/// after the approval gate is granted and both parallel children (invoice + ship) complete.
/// </summary>
/// <remarks>
/// STATELESS on purpose — Step Output Forwarding (cross-step parameter propagation) is a v0.2 concern,
/// so this job reads NO inbound parameters and produces NO output for the next step. The batch step's
/// <c>OnService("notification")</c> routes to this worker by service name; the job NAME is
/// <c>"SendNotification"</c> (the <see cref="JobAttribute.Name"/> below), which the server's batch
/// definition references as the step's <c>JobName</c>.
/// </remarks>
[Job(Name = "SendNotification")]
public sealed class SendNotificationJob : IJob
{
    private readonly ILogger<SendNotificationJob> _logger;

    public SendNotificationJob(ILogger<SendNotificationJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger.LogInformation(
            "SendNotificationJob (notification worker): received cross-service invocation from source={Source} over RabbitMQ.",
            context.TriggeredBy);

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync demonstrates
        // synchronous wait-for-result semantics over the broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "SendNotificationJob (notification worker): completed — returning Completed status to the server via direct-reply-to.");
    }
}
