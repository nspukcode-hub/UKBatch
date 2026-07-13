using UKBatch.Abstractions.Jobs;

namespace Sample.WorkerMode.Invoicing.Jobs;

/// <summary>
/// Cross-service job invoked by the server's batch step 1 over the RabbitMQ broker. Generates an invoice
/// and forwards its details to the next step: the produced <c>invoiceId</c> (scalar) and <c>invoice</c>
/// (object) are returned to the orchestrator on the reply and merged into the shipping step's parameters,
/// so the shipping worker reads the real invoice rather than a placeholder.
/// </summary>
/// <remarks>
/// The batch step's <c>OnService("invoicing")</c> routes to this worker by service name; the job NAME is
/// <c>"GenerateInvoice"</c> (the <see cref="JobAttribute.Name"/> below), which the server's batch definition
/// references as the step's <c>JobName</c>. <see cref="Invoice"/> is defined identically on the shipping
/// worker so the object round-trips as JSON across the service boundary (cross-service objects need a shared shape).
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

        // Produce this step's outputs. They ride the reply back to the orchestrator (only on success) and
        // are forwarded into the next step's parameters. A scalar (invoiceId) and an object (invoice) both
        // cross the service boundary as JSON — set them AFTER the fail check so a failed step forwards nothing.
        var invoiceId = $"INV-{Random.Shared.Next(10000, 99999)}";
        var invoice = new Invoice(invoiceId, "Acme Corporation", 1499.90m);
        context.Outputs.Set("invoiceId", invoiceId);
        context.Outputs.Set("invoice", invoice);
        // Forward the amount as a top-level scalar so a later step can guard on it with a run-if condition
        // (nested keys like "invoice.amount" are not addressable). A trigger parameter overrides the invoice's
        // own amount, so a demo can drive the shipping step's condition either way (skip vs run).
        context.Outputs.Set("amount", context.Parameters.GetOrDefault<decimal>("amount", invoice.Amount));

        _logger.LogInformation(
            "GenerateInvoiceJob (invoicing worker): completed — produced invoiceId={InvoiceId} for {Customer} (amount {Amount:C}); returning it to the server via direct-reply-to.",
            invoiceId, invoice.Customer, invoice.Amount);
    }
}
