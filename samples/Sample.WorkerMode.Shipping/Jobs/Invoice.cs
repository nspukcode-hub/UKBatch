namespace Sample.WorkerMode.Shipping.Jobs;

/// <summary>
/// Invoice details forwarded from the invoicing worker to <see cref="ShipOrderJob"/> across the service
/// boundary. Matches the invoicing worker's <c>Invoice</c> shape (same property names) so the object
/// deserializes from the JSON that crossed the boundary — a cross-service object needs a matching shape.
/// </summary>
public sealed record Invoice(string Id, string Customer, decimal Amount);
