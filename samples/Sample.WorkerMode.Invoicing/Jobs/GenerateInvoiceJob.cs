using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Invoicing.Jobs;

/// <summary>
/// Cross-service job invoked by the server's batch step 1 over the RabbitMQ broker. "Generates an
/// invoice" (no-op log + short sleep) and returns a Completed status to the orchestrator via the
/// request/reply (direct-reply-to) path.
/// </summary>
/// <remarks>
/// STATELESS on purpose — Step Output Forwarding (cross-step parameter propagation) is a v0.2 concern,
/// so this job reads NO inbound parameters and produces NO output for the next step. The batch step's
/// <c>OnService("invoicing")</c> routes to this worker by service name; the job NAME is
/// <c>"GenerateInvoice"</c> (the <see cref="JobAttribute.Name"/> below), which the server's batch
/// definition references as the step's <c>JobName</c>.
/// </remarks>
[Job(Name = "GenerateInvoice")]
public sealed class GenerateInvoiceJob : IJob
{
    private readonly ILogger<GenerateInvoiceJob> _logger;

    public GenerateInvoiceJob(ILogger<GenerateInvoiceJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger.LogInformation(
            "GenerateInvoiceJob (invoicing worker): received cross-service invocation from source={Source} over RabbitMQ.",
            context.TriggeredBy);

        // e2e onFailure/compensation hook (scenario S5): when the batch step passes { "fail": "true" } in
        // job.parameters, throw so the orchestrator's Compensate policy routes to OnFailureSteps. Absent
        // by default → normal success, so the other demos (sequential / approval+parallel / durability)
        // are unaffected. The value crosses the broker as object? (a JsonElement), so compare ToString().
        if (context.Parameters.Values.TryGetValue("fail", out var failFlag)
            && string.Equals(failFlag?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("GenerateInvoiceJob (invoicing worker): fail=true injected — throwing to exercise compensation.");
            throw new InvalidOperationException(
                "GenerateInvoiceJob: injected failure (fail=true) for the onFailure/compensation e2e demo.");
        }

        // Simulate work — concrete time so the orchestrator's RequestReplyAsync demonstrates
        // synchronous wait-for-result semantics over the broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "GenerateInvoiceJob (invoicing worker): completed — returning Completed status to the server via direct-reply-to.");
    }
}
