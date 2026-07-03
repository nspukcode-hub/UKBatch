namespace Sample.WorkerMode.Invoicing.Jobs;

/// <summary>
/// Invoice details produced by <see cref="GenerateInvoiceJob"/> and forwarded across the service boundary
/// to the shipping worker. Defined identically (same property names) on the shipping worker so it
/// round-trips as JSON — a cross-service object needs a matching shape on both sides.
/// </summary>
public sealed record Invoice(string Id, string Customer, decimal Amount);
